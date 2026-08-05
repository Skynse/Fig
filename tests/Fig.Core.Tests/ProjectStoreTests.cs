using Fig.Core.Project;

namespace Fig.Core.Tests;

public class ProjectStoreTests
{
    private static ProjectStore CreateStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"fig_store_{Guid.NewGuid():N}");
        return new ProjectStore(root);
    }

    [Fact]
    public void CreateProject_MakesFolderWithProjectJson()
    {
        var store = CreateStore(out var root);
        try
        {
            var id = store.CreateProject("Test Proj");

            Assert.True(Directory.Exists(store.ProjectDirectory(id)));
            Assert.True(File.Exists(Path.Combine(store.ProjectDirectory(id), "project.json")));
            Assert.True(Directory.Exists(store.CacheDirectory(id)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ListProjects_ReturnsSavedProjects()
    {
        var store = CreateStore(out var root);
        try
        {
            var id = store.CreateProject("A");
            var project = store.LoadProject(id)!;
            project.Name = "Renamed";
            store.SaveProject(project);

            var list = store.ListProjects();

            Assert.Single(list);
            Assert.Equal(id, list[0].Id);
            Assert.Equal("Renamed", list[0].Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadProject_SaveRoundTrip_PreservesName()
    {
        var store = CreateStore(out var root);
        try
        {
            var id = store.CreateProject("Round");
            var loaded = store.LoadProject(id);

            Assert.NotNull(loaded);
            Assert.Equal("Round", loaded!.Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CreateProject_UsesReadableFolderId()
    {
        var store = CreateStore(out var root);
        try
        {
            var id = store.CreateProject("My Cool Edit");

            Assert.StartsWith("My-Cool-Edit_", id);
            Assert.True(Directory.Exists(store.ProjectDirectory(id)));
            var loaded = store.LoadProject(id)!;
            Assert.Equal("My Cool Edit", loaded.Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadProject_Missing_ReturnsNull()
    {
        var store = CreateStore(out var root);
        try
        {
            Assert.Null(store.LoadProject("nonexistent"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
