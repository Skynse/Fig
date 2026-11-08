//! Decodes one frame of a file to a PNG, to look at: `cargo run -p fig-media --example frame -- in.mp4 0.5 out.png`.

fn main() {
    let a: Vec<String> = std::env::args().collect();
    let asset = fig_media::probe::probe(a[1].as_ref()).unwrap();
    println!("{}×{} {:?} {:.3}s", asset.width, asset.height, asset.kind, asset.duration_sec);
    let (w, h) = (asset.width as usize, asset.height as usize);
    let mut v = fig_media::VideoSource::open(a[1].as_ref(), w, h).unwrap();
    let f = v.decode_forward(a[2].parse().unwrap(), fig_media::DecodeMode::Playback).unwrap();
    image::save_buffer(&a[3], &f.to_rgba(), w as u32, h as u32, image::ColorType::Rgba8).unwrap();
}
