using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public ObservableCollection<ExportJob> Jobs { get; } = new();

        public ExportJobRunner(MediaService media)
        {
            _media = media;
        }

        public ExportJob Enqueue(string outputPath, int width, int height, int crf,
            ProjectModel project, TimelineModel timeline)
        {
            var job = new ExportJob(outputPath, width, height, timeline.Rate.Fps);
            Jobs.Add(job);
            _ = RunAsync(job, project, timeline, crf);
            return job;
        }

        private async Task RunAsync(ExportJob job, ProjectModel project, TimelineModel timeline, int crf)
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed)
                    return;
                job.Status = ExportJobStatus.Running;
                try
                {
                    await Task.Run(() => _media.RenderTimeline(
                        project, timeline, job.OutputPath, job.Width, job.Height, crf,
                        p => Dispatcher.UIThread.Post(() => job.Progress = p)));
                    job.Status = ExportJobStatus.Done;
                    job.Progress = 1;
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
