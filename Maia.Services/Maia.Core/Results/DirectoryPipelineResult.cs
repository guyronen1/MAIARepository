namespace Maia.Core.Results;

public sealed class DirectoryPipelineResult
{
    public required string DirectoryPath { get; init; }
    public int FilesScanned { get; set; }
    public int JobsCreated { get; set; }
    public int Classifications { get; set; }
    public int Recommendations { get; set; }
}
