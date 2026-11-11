using IndexStoragePoc;

// Vault Index storage PoC. Runs against the local container in ../docker (SQL Server 2025 + Full-Text Search).
// POC_SA holds the container's sa password; nothing here targets a shared database.
var sa = Environment.GetEnvironmentVariable("POC_SA") ?? throw new InvalidOperationException("Set POC_SA.");
var server = Environment.GetEnvironmentVariable("POC_SERVER") ?? "localhost,14333";
var master = $"Server={server};User ID=sa;Password={sa};TrustServerCertificate=true;Database=master";
var poc = $"Server={server};User ID=sa;Password={sa};TrustServerCertificate=true;Database=IndexPoc";

switch (args.FirstOrDefault())
{
    case "seed":
        await Seed.RunAsync(master, poc, int.Parse(args[1]), int.Parse(args[2]));
        break;
    case "vector":
        await VectorBench.RunAsync(poc);
        break;
    case "ftrank":
        await RankedFullTextBench.RunAsync(poc);
        break;
    case "ftlag":
        await FullTextLagBench.RunAsync(poc);
        break;
    case "ftjp":
        await JapaneseFullText.RunAsync(poc);
        break;
    case "fields":
        await FieldFilterBench.RunAsync(poc);
        break;
    case "ingest":
        await IngestionProbe.RunAsync(poc);
        break;
    default:
        Console.WriteLine("seed <mainChunks> <otherChunks> | vector | ftrank | ftlag | ftjp | fields | ingest");
        break;
}
