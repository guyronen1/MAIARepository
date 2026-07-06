namespace Maia.Core.Enums;

public enum ScanType
{
    FileSystem  = 0,   // scan log files in LogFolder matching SearchPatterns
    Database    = 1,   // query SourceTable and check CheckColumn against [RangeMin, RangeMax]
    ApiEndpoint = 2,   // poll LogSourceUrl and inspect the response for errors
    FileContent = 3    // structured extraction from input data files (XML, …) in LogFolder
}
