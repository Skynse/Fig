using Fig.App.ViewModels;
using Fig.Core.Project;
using Fig.Core.Timeline;
using TimelineModel=Fig.Core.Timeline.Timeline;
namespace Fig.App.Tests;

public sealed class ExportAndHomeTests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),"fig-tests-"+Guid.NewGuid().ToString("N"));
    public ExportAndHomeTests()=>Directory.CreateDirectory(_folder);
    private static TimelineModel Timeline()=>new(){Rate=new FrameRate(30000,1001),Tracks=[new Track{Kind=TrackKind.Video,Clips=[new VideoClip{DurSec=10}]}],Markers=[new Marker{Name="Red",StartSec=1,Color="#FF6B6B"},new Marker{Name="Blue",StartSec=4,Color="#5F9CF8"}]};
    [Fact] public void ColorFilter_KeepsNextDifferentColorBoundary()
    {
        var timeline=Timeline();var vm=new ExportDialogViewModel(timeline.Rate.Fps,1920,1080,timeline){ChapterMode=true,OutputPath=_folder};
        vm.SelectColor("#FF6B6B",add:false);
        Assert.Single(vm.Chapters,c=>c.IsIncluded);Assert.True(vm.CanExport);
        var chapter=Assert.Single(vm.CreateOptions().Chapters!);
        Assert.Equal(timeline.Rate.ToSeconds(timeline.Rate.ToFrame(4)),chapter.EndSec);
    }
    [Fact] public void Chapters_ValidateCollisionsAndRequireASelection()
    {
        var vm=new ExportDialogViewModel(30,1920,1080,Timeline()){ChapterMode=true,OutputPath=_folder};
        Assert.True(vm.CanExport);vm.Chapters[1].FileName=vm.Chapters[0].FileName;Assert.False(vm.CanExport);
        vm.Chapters[1].IsIncluded=false;Assert.True(vm.CanExport);
        File.WriteAllText(Path.Combine(_folder,vm.Chapters[0].FileName),"existing");Assert.False(vm.CanExport);
        File.Delete(Path.Combine(_folder,vm.Chapters[0].FileName));vm.Chapters[0].IsIncluded=false;Assert.False(vm.CanExport);
    }
    public void Dispose()=>Directory.Delete(_folder,true);
}
