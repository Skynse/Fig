using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fig.App.Rendering;
using Fig.App.ViewModels;
using Fig.Core.Media;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.Services
{
    /// <summary>
    /// Runs timeline exports as background jobs, one at a time. Each job reports progress
    /// (marshalled to the UI thread) and finishes as Done or Failed with a message.
    /// </summary>
    public sealed class ExportJobRunner : IDisposable
    {
        private readonly MediaService _media;
        private readonly Func<RendererChoice> _renderer;
        private readonly ILayerRasterizer? _layers;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public ObservableCollection<ExportJob> Jobs { get; } = new();

        /// <param name="renderer">Reads the user's renderer preference when a job starts.</param>
        public ExportJobRunner(MediaService media, Func<RendererChoice>? renderer = null, ILayerRasterizer? layers = null)
        {
            _media = media;
            _layers = layers;
            _renderer = renderer ?? (() => RendererChoice.Automatic);
        }

        public ExportJob Enqueue(string outputPath, int width, int height, int crf,
            ProjectModel project, TimelineModel timeline, double? startSec = null, double? endSec = null,
            Fig.Core.Media.VideoEncoderChoice encoder = Fig.Core.Media.VideoEncoderChoice.Automatic,
            Fig.Core.Media.ExportTurn turn = Fig.Core.Media.ExportTurn.None)
        {
            // the job shows the file's size: tall when the wide picture is turned sideways
            var job = turn == Fig.Core.Media.ExportTurn.None
                ? new ExportJob(outputPath, width, height, timeline.Rate.Fps)
                : new ExportJob(outputPath, height, width, timeline.Rate.Fps);
            Jobs.Add(job);
            _ = RunAsync(job, project, timeline, width, height, crf, startSec, endSec, encoder, turn);
            return job;
        }

        private async Task RunAsync(ExportJob job, ProjectModel project, TimelineModel timeline, int width, int height, int crf,
            double? startSec, double? endSec, Fig.Core.Media.VideoEncoderChoice encoder, Fig.Core.Media.ExportTurn turn)
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || job.Cancellation.IsCancellationRequested)
                    return;
                job.Status = ExportJobStatus.Running;
                try
                {
                    // the GPU when the window has one, the CPU kernels otherwise: same scenes, same picture
                    var renderer = await SkiaSceneRenderer.CreateAsync(_renderer());
                    await Task.Run(() => _media.RenderTimeline(
                        project, timeline, job.OutputPath, width, height, crf,
                        p => Dispatcher.UIThread.Post(() => job.Progress = p), startSec, endSec, renderer, _layers,
                        encoder, name => Dispatcher.UIThread.Post(() => job.EncoderName = name), turn, job.Cancellation.Token));
                    job.Status = ExportJobStatus.Done;
                    job.Progress = 1;
                }
                catch (OperationCanceledException)
                {
                    job.Status = ExportJobStatus.Cancelled;
                    job.IsCancelling = false;
                    TryDeleteOutput(job.OutputPath);
                }
                catch (Exception ex)
                {
                    job.Status = ExportJobStatus.Failed;
                    job.Error = ex.Message;
                    TryDeleteOutput(job.OutputPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static void TryDeleteOutput(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best-effort: leave the partial file for inspection
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _gate.Dispose();
        }
    }
}
