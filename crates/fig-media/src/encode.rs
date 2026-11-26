//! Writing MP4s: an H.264 video track (a GPU encoder when there is one, else x264) and an AAC sound track. Export,
//! proxies and reversed copies all write through here.

use std::path::Path;

use ff::ffi;
use ff::format::Pixel;
use ff::software::scaling;
use ffmpeg_next as ff;
use fig_timeline::FrameRate;

use crate::MediaError;

/// Which H.264 encoder an export uses.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default)]
pub enum EncoderChoice {
    /// The first GPU encoder that opens on this machine, else the CPU.
    #[default]
    Automatic,
    /// NVIDIA NVENC.
    Nvenc,
    /// Intel Quick Sync.
    QuickSync,
    /// AMD AMF (Windows).
    Amf,
    /// VA-API: AMD and Intel GPUs on Linux.
    Vaapi,
    /// libx264 on the CPU: slowest, smallest files at a given quality.
    Software,
}

const HARDWARE: [(EncoderChoice, &str, &str); 4] = [
    (EncoderChoice::Nvenc, "h264_nvenc", "NVIDIA NVENC"),
    (EncoderChoice::QuickSync, "h264_qsv", "Intel Quick Sync"),
    (EncoderChoice::Amf, "h264_amf", "AMD AMF"),
    (EncoderChoice::Vaapi, "h264_vaapi", "VA-API"),
];

/// The encoders to try, in order: the chosen one (or every GPU one for Automatic), then the CPU.
pub fn candidates(choice: EncoderChoice) -> Vec<(&'static str, &'static str)> {
    let mut list: Vec<(&str, &str)> = HARDWARE
        .iter()
        .filter(|(c, _, _)| choice == EncoderChoice::Automatic || choice == *c)
        .map(|(_, n, l)| (*n, *l))
        .collect();
    list.push(("libx264", "CPU (x264)"));
    list
}

/// How a video track is encoded.
#[derive(Clone, Debug)]
pub struct VideoSettings {
    pub width: u32,
    pub height: u32,
    pub rate: FrameRate,
    pub encoder: EncoderChoice,
    /// Constant quality, 0 best to 51 worst (each encoder's own scale is mapped from it).
    pub quality: u32,
    /// Frames between keyframes.
    pub gop: u32,
    /// For x264: its speed preset ("veryfast"), and tune ("fastdecode"), when set.
    pub preset: Option<&'static str>,
    pub tune: Option<&'static str>,
}

struct VideoTrack {
    encoder: ff::encoder::Video,
    stream: usize,
    time_base: ff::Rational,
    format: Pixel,
    scaler: scaling::Context,
    /// For VA-API: the pool of GPU frames each frame is uploaded into.
    hw_frames: *mut ffi::AVBufferRef,
    hw_device: *mut ffi::AVBufferRef,
    next_pts: i64,
    label: &'static str,
}

struct AudioTrack {
    encoder: ff::encoder::Audio,
    stream: usize,
    time_base: ff::Rational,
    frame_size: usize,
    /// Interleaved stereo samples not yet sent (less than a frame's worth).
    pending: Vec<f32>,
    next_pts: i64,
}

/// An MP4 being written.
pub struct Mp4Writer {
    output: ff::format::context::Output,
    video: Option<VideoTrack>,
    audio: Option<AudioTrack>,
    started: bool,
}

// the hardware buffers are only touched by the thread that owns the writer
unsafe impl Send for Mp4Writer {}

pub const AUDIO_RATE: u32 = 48_000;

impl Mp4Writer {
    pub fn create(path: &Path) -> Result<Mp4Writer, MediaError> {
        crate::init();
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let output = ff::format::output(path)?;
        Ok(Mp4Writer { output, video: None, audio: None, started: false })
    }

    fn global_header(&self) -> bool {
        self.output.format().flags().contains(ff::format::Flags::GLOBAL_HEADER)
    }

    /// Adds the video track: the first candidate encoder that opens. Returns the encoder's name for people.
    pub fn add_video(&mut self, s: &VideoSettings) -> Result<&'static str, MediaError> {
        for (name, label) in candidates(s.encoder) {
            if let Some(track) = self.try_video(name, label, s) {
                self.video = Some(track);
                return Ok(label);
            }
        }
        Err(MediaError::Other("No H.264 encoder could be opened (libx264 is missing from this FFmpeg)".into()))
    }

    fn try_video(&mut self, name: &str, label: &'static str, s: &VideoSettings) -> Option<VideoTrack> {
        let codec = ff::encoder::find_by_name(name)?;
        let global = self.global_header();
        let mut ctx = ff::codec::context::Context::new_with_codec(codec).encoder().video().ok()?;
        ctx.set_width(s.width);
        ctx.set_height(s.height);
        ctx.set_gop(s.gop.max(1));
        ctx.set_max_b_frames(0);
        // the timeline's exact rate: 29.97 is 30000/1001, never rounded to 30
        let time_base = ff::Rational::new(s.rate.den(), s.rate.num());
        ctx.set_time_base(time_base);
        ctx.set_frame_rate(Some(ff::Rational::new(s.rate.num(), s.rate.den())));
        if global {
            ctx.set_flags(ff::codec::Flags::GLOBAL_HEADER);
        }
        let q = s.quality.min(51).to_string();
        let mut opts = ff::Dictionary::new();
        let mut hw = (std::ptr::null_mut(), std::ptr::null_mut());
        let format = match name {
            "h264_nvenc" => {
                opts.set("preset", "p5");
                opts.set("rc", "vbr");
                opts.set("cq", &q);
                ctx.set_bit_rate(0);
                Pixel::YUV420P
            }
            "h264_qsv" => {
                unsafe { (*ctx.as_mut_ptr()).global_quality = s.quality.clamp(1, 51) as i32 };
                Pixel::NV12
            }
            "h264_amf" => {
                opts.set("rc", "cqp");
                opts.set("qp_i", &q);
                opts.set("qp_p", &q);
                Pixel::NV12
            }
            "h264_vaapi" => {
                hw = unsafe { vaapi_frames(ctx.as_mut_ptr(), s.width, s.height)? };
                opts.set("rc_mode", "CQP");
                opts.set("qp", &q);
                Pixel::NV12
            }
            _ => {
                opts.set("crf", &q);
                if let Some(p) = s.preset {
                    opts.set("preset", p);
                }
                if let Some(t) = s.tune {
                    opts.set("tune", t);
                }
                Pixel::YUV420P
            }
        };
        if hw.0.is_null() {
            ctx.set_format(format);
        }
        let encoder = match ctx.open_with(opts) {
            Ok(e) => e,
            Err(_) => {
                unsafe { free_hw(hw) };
                return None;
            }
        };
        let stream = {
            let mut st = self.output.add_stream(codec).ok()?;
            st.set_parameters(&encoder);
            st.set_time_base(time_base);
            st.index()
        };
        let scaler =
            scaling::Context::get(Pixel::BGRA, s.width, s.height, format, s.width, s.height, scaling::Flags::BILINEAR)
                .ok()?;
        Some(VideoTrack {
            encoder,
            stream,
            time_base,
            format,
            scaler,
            hw_frames: hw.0,
            hw_device: hw.1,
            next_pts: 0,
            label,
        })
    }

    /// Adds an AAC stereo track at 48 kHz.
    pub fn add_audio(&mut self) -> Result<(), MediaError> {
        let codec =
            ff::encoder::find(ff::codec::Id::AAC).ok_or_else(|| MediaError::Other("AAC encoder not found".into()))?;
        let global = self.global_header();
        let mut ctx = ff::codec::context::Context::new_with_codec(codec).encoder().audio()?;
        ctx.set_rate(AUDIO_RATE as i32);
        ctx.set_channel_layout(ff::util::channel_layout::ChannelLayout::default(2));
        ctx.set_format(ff::format::Sample::F32(ff::format::sample::Type::Planar));
        ctx.set_bit_rate(192_000);
        let time_base = ff::Rational::new(1, AUDIO_RATE as i32);
        ctx.set_time_base(time_base);
        if global {
            ctx.set_flags(ff::codec::Flags::GLOBAL_HEADER);
        }
        let encoder = ctx.open()?;
        let frame_size = (encoder.frame_size() as usize).max(1);
        let stream = {
            let mut st = self.output.add_stream(codec)?;
            st.set_parameters(&encoder);
            st.set_time_base(time_base);
            st.index()
        };
        self.audio = Some(AudioTrack { encoder, stream, time_base, frame_size, pending: Vec::new(), next_pts: 0 });
        Ok(())
    }

    /// The video encoder in use, for people ("NVIDIA NVENC").
    pub fn video_label(&self) -> Option<&'static str> {
        self.video.as_ref().map(|v| v.label)
    }

    fn start(&mut self) -> Result<(), MediaError> {
        if !self.started {
            let mut opts = ff::Dictionary::new();
            // the index up front, so a player can start before the whole file has arrived
            opts.set("movflags", "+faststart");
            self.output.write_header_with(opts)?;
            self.started = true;
        }
        Ok(())
    }

    /// Encodes the next video frame from BGRA pixels the track's size (`stride` bytes a row).
    pub fn write_bgra(&mut self, pixels: &[u8], stride: usize) -> Result<(), MediaError> {
        self.start()?;
        let v = self.video.as_mut().ok_or_else(|| MediaError::Other("no video track".into()))?;
        let (w, h) = (v.encoder.width(), v.encoder.height());
        let mut bgra = ff::frame::Video::new(Pixel::BGRA, w, h);
        let dst_stride = bgra.stride(0);
        let row = w as usize * 4;
        let data = bgra.data_mut(0);
        for y in 0..h as usize {
            data[y * dst_stride..y * dst_stride + row].copy_from_slice(&pixels[y * stride..y * stride + row]);
        }
        let mut yuv = ff::frame::Video::new(v.format, w, h);
        v.scaler.run(&bgra, &mut yuv)?;
        yuv.set_pts(Some(v.next_pts));
        v.next_pts += 1;
        if v.hw_frames.is_null() {
            v.encoder.send_frame(&yuv)?;
        } else {
            // VA-API encodes GPU frames: upload this one into the encoder's pool first
            let mut gpu = ff::frame::Video::empty();
            unsafe {
                if ffi::av_hwframe_get_buffer(v.hw_frames, gpu.as_mut_ptr(), 0) < 0
                    || ffi::av_hwframe_transfer_data(gpu.as_mut_ptr(), yuv.as_ptr(), 0) < 0
                {
                    return Err(MediaError::Other("couldn't upload a frame to the GPU".into()));
                }
            }
            gpu.set_pts(yuv.pts());
            v.encoder.send_frame(&gpu)?;
        }
        Self::drain_video(&mut self.output, v)
    }

    fn drain_video(output: &mut ff::format::context::Output, v: &mut VideoTrack) -> Result<(), MediaError> {
        let mut packet = ff::Packet::empty();
        while v.encoder.receive_packet(&mut packet).is_ok() {
            packet.set_stream(v.stream);
            let st_tb = output.stream(v.stream).unwrap().time_base();
            packet.rescale_ts(v.time_base, st_tb);
            packet.write_interleaved(output)?;
        }
        Ok(())
    }

    /// Queues interleaved stereo samples at 48 kHz; full encoder frames go out as they fill.
    pub fn write_audio(&mut self, interleaved: &[f32]) -> Result<(), MediaError> {
        self.start()?;
        let Some(a) = self.audio.as_mut() else { return Ok(()) };
        a.pending.extend_from_slice(interleaved);
        while a.pending.len() >= a.frame_size * 2 {
            let chunk: Vec<f32> = a.pending.drain(..a.frame_size * 2).collect();
            Self::send_audio(&mut self.output, a, &chunk)?;
        }
        Ok(())
    }

    fn send_audio(
        output: &mut ff::format::context::Output,
        a: &mut AudioTrack,
        interleaved: &[f32],
    ) -> Result<(), MediaError> {
        let n = interleaved.len() / 2;
        let mut frame = ff::frame::Audio::new(
            ff::format::Sample::F32(ff::format::sample::Type::Planar),
            n,
            ff::util::channel_layout::ChannelLayout::default(2),
        );
        frame.set_rate(AUDIO_RATE);
        for c in 0..2 {
            let plane = frame.plane_mut::<f32>(c);
            for i in 0..n {
                plane[i] = interleaved[i * 2 + c];
            }
        }
        frame.set_pts(Some(a.next_pts));
        a.next_pts += n as i64;
        a.encoder.send_frame(&frame)?;
        Self::drain_audio(output, a)
    }

    fn drain_audio(output: &mut ff::format::context::Output, a: &mut AudioTrack) -> Result<(), MediaError> {
        let mut packet = ff::Packet::empty();
        while a.encoder.receive_packet(&mut packet).is_ok() {
            packet.set_stream(a.stream);
            let st_tb = output.stream(a.stream).unwrap().time_base();
            packet.rescale_ts(a.time_base, st_tb);
            packet.write_interleaved(output)?;
        }
        Ok(())
    }

    /// Flushes the encoders and finishes the file.
    pub fn finish(mut self) -> Result<(), MediaError> {
        self.start()?;
        if let Some(v) = self.video.as_mut() {
            v.encoder.send_eof()?;
            Self::drain_video(&mut self.output, v)?;
        }
        if let Some(a) = self.audio.as_mut() {
            if !a.pending.is_empty() {
                let rest = std::mem::take(&mut a.pending);
                Self::send_audio(&mut self.output, a, &rest)?;
            }
            a.encoder.send_eof()?;
            Self::drain_audio(&mut self.output, a)?;
        }
        self.output.write_trailer()?;
        Ok(())
    }
}

impl Drop for Mp4Writer {
    fn drop(&mut self) {
        if let Some(v) = self.video.as_mut() {
            unsafe { free_hw((v.hw_frames, v.hw_device)) };
            v.hw_frames = std::ptr::null_mut();
            v.hw_device = std::ptr::null_mut();
        }
    }
}

/// VA-API encodes GPU frames: a device, and a pool of NV12 frames on it each frame is uploaded into.
unsafe fn vaapi_frames(
    ctx: *mut ffi::AVCodecContext,
    w: u32,
    h: u32,
) -> Option<(*mut ffi::AVBufferRef, *mut ffi::AVBufferRef)> {
    unsafe {
        let mut device: *mut ffi::AVBufferRef = std::ptr::null_mut();
        if ffi::av_hwdevice_ctx_create(
            &mut device,
            ffi::AVHWDeviceType::AV_HWDEVICE_TYPE_VAAPI,
            std::ptr::null(),
            std::ptr::null_mut(),
            0,
        ) < 0
        {
            return None;
        }
        let mut frames = ffi::av_hwframe_ctx_alloc(device);
        if frames.is_null() {
            ffi::av_buffer_unref(&mut device);
            return None;
        }
        let fc = (*frames).data as *mut ffi::AVHWFramesContext;
        (*fc).format = ffi::AVPixelFormat::AV_PIX_FMT_VAAPI;
        (*fc).sw_format = ffi::AVPixelFormat::AV_PIX_FMT_NV12;
        (*fc).width = w as i32;
        (*fc).height = h as i32;
        (*fc).initial_pool_size = 16;
        if ffi::av_hwframe_ctx_init(frames) < 0 {
            ffi::av_buffer_unref(&mut frames);
            ffi::av_buffer_unref(&mut device);
            return None;
        }
        (*ctx).pix_fmt = ffi::AVPixelFormat::AV_PIX_FMT_VAAPI;
        (*ctx).hw_frames_ctx = ffi::av_buffer_ref(frames);
        Some((frames, device))
    }
}

unsafe fn free_hw((mut frames, mut device): (*mut ffi::AVBufferRef, *mut ffi::AVBufferRef)) {
    unsafe {
        if !frames.is_null() {
            ffi::av_buffer_unref(&mut frames);
        }
        if !device.is_null() {
            ffi::av_buffer_unref(&mut device);
        }
    }
}
