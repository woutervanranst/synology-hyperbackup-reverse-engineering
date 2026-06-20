using System.Text;
using HyperBackup.Core;
using HyperBackup.Core.Archive;
using HyperBackup.Core.Model;
using HyperBackup.Core.Restore;
using HyperBackup.Core.Storage;

namespace HyperBackup.Cli;

/// <summary>Parses arguments, builds a repository, and dispatches to the commands.</summary>
public static class CommandRunner
{
    public static int Run(string[] argv)
    {
        var args = Args.Parse(argv);
        try
        {
            switch (args.Command.ToLowerInvariant())
            {
                case "ls": return Ls(args);
                case "get": return Get(args);
                case "keytest": return KeyTest(args);
                case "info": return Info(args);
                case "list": return List(args);
                case "restore": return Restore(args);
                case "archive-plan": return ArchivePlanCmd(args);
                case "rehydrate": return Rehydrate(args);
                case "demo": return Demo(args);
                case "help" or "--help" or "-h": Help(); return 0;
                default:
                    Console.Error.WriteLine($"Unknown command: {args.Command}\n");
                    Help();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (args.Flag("verbose"))
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // --- Repository construction ---

    private static BackupRepository BuildRepo(Args args)
    {
        var storage = BuildStorage(args);
        var passphrase = args.Get("passphrase", "HBK_PASSPHRASE");
        byte[]? rsaKey = null;
        var rsaPath = args.Get("rsa-key");
        if (rsaPath is not null)
            rsaKey = File.ReadAllBytes(rsaPath);
        return new BackupRepository(storage, passphrase, rsaKey);
    }

    private static IBackupStorage BuildStorage(Args args)
    {
        var local = args.Get("local");
        if (local is not null)
            return new LocalDirectoryStorage(local);

        var prefix = args.Get("azure-prefix") ?? "";
        var sas = args.Get("azure-sas", "HBK_AZURE_SAS");
        if (sas is not null)
            return AzureBlobStorage.FromContainerSasUrl(sas, prefix);

        var conn = args.Get("azure-connection-string", "HBK_AZURE_CONNECTION_STRING");
        var container = args.Get("azure-container") ?? "hyperbackup";
        if (conn is not null)
            return AzureBlobStorage.FromConnectionString(conn, container, prefix);

        var account = args.Get("azure-account");
        var key = args.Get("azure-key", "HBK_AZURE_KEY");
        if (account is not null && key is not null)
            return AzureBlobStorage.FromAccountKey(account, key, container, prefix);

        throw new ArgumentException(
            "No storage specified. Use --local <dir>, or --azure-account/--azure-key (+--azure-container), " +
            "or --azure-connection-string, or --azure-sas.");
    }

    // --- Commands ---

    /// <summary>Raw blob listing for discovery (no repository parsing required).</summary>
    private static int Ls(Args args)
    {
        var storage = BuildStorage(args);
        var prefix = args.Get("prefix") ?? "";
        Console.WriteLine($"Listing {storage.Description} prefix='{prefix}'\n");

        long total = 0;
        var count = 0;
        var sizeHistogram = new Dictionary<long, int>();
        var byTier = new Dictionary<string, (int Count, long Bytes)>();
        var limit = args.GetInt("limit") ?? 200;

        foreach (var b in storage.ListInfos(prefix).OrderBy(b => b.Path, StringComparer.Ordinal))
        {
            if (count < limit)
                Console.WriteLine($"{HumanSize(b.Size),12}  {b.Tier,-8} {(b.RehydratePending ? "rehyd " : "      ")}{b.Path}");
            else if (count == limit)
                Console.WriteLine($"  … (--limit {limit} reached; totals below cover everything)");
            total += b.Size;
            count++;
            sizeHistogram[b.Size] = sizeHistogram.GetValueOrDefault(b.Size) + 1;
            var t = b.Tier.ToString();
            var agg = byTier.GetValueOrDefault(t);
            byTier[t] = (agg.Count + 1, agg.Bytes + b.Size);
        }

        Console.WriteLine($"\n{count} blobs, {HumanSize(total)} total");
        Console.WriteLine("by tier:");
        foreach (var (t, agg) in byTier.OrderByDescending(kv => kv.Value.Bytes))
            Console.WriteLine($"  {t,-8} {agg.Count,6} blobs  {HumanSize(agg.Bytes)}");

        var repeated = sizeHistogram.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).Take(8).ToList();
        if (repeated.Count > 0)
        {
            Console.WriteLine("repeated blob sizes (concatenation/fixed-capacity hint):");
            foreach (var kv in repeated)
                Console.WriteLine($"  {kv.Value,5} blobs of exactly {kv.Key:N0} bytes ({HumanSize(kv.Key)})");
        }
        return 0;
    }

    /// <summary>Download a single raw blob to a local file (for inspection).</summary>
    private static int Get(Args args)
    {
        var storage = BuildStorage(args);
        var blob = args.Require("blob");
        var outp = args.Get("out") ?? Path.GetFileName(blob);
        var full = Path.GetFullPath(outp);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var offset = args.GetInt("offset");
        var length = args.GetInt("length");
        if (offset is not null && length is not null)
        {
            var bytes = storage.ReadRange(blob, offset.Value, length.Value);
            File.WriteAllBytes(full, bytes);
        }
        else
        {
            using var s = storage.OpenRead(blob);
            using var f = File.Create(full);
            s.CopyTo(f);
        }
        Console.WriteLine($"downloaded {blob} -> {outp} ({new FileInfo(full).Length} bytes)");
        return 0;
    }

    private static int KeyTest(Args args)
    {
        var repo = BuildRepo(args);
        var keys = repo.RequireKeys();
        Console.WriteLine("derived public key: " + Convert.ToHexString(keys.PublicKey).ToLowerInvariant());
        foreach (var v in repo.ListVersions())
        {
            try
            {
                var vk = repo.GetVersionKey(v.Id);
                Console.WriteLine($"v{v.Id}: file_key={Convert.ToHexString(vk.FileKey).ToLowerInvariant()} file_iv={Convert.ToHexString(vk.FileIv).ToLowerInvariant()}");
            }
            catch (Exception ex) { Console.WriteLine($"v{v.Id}: {ex.Message}"); }
        }
        return 0;
    }

    private static int Info(Args args)
    {
        var repo = BuildRepo(args);
        Console.WriteLine($"Repository: {repo.Storage.Description}");
        Console.WriteLine($"  encrypted : {repo.IsEncrypted}");
        Console.WriteLine($"  compressed: {repo.IsCompressed}");
        Console.WriteLine($"  xattr     : {repo.XattrEnabled}");
        Console.WriteLine($"  unikey    : {repo.Unikey}");
        if (repo.IsEncrypted)
            Console.WriteLine($"  keys      : {(repo.TryGetKeys() is not null ? "available" : "UNAVAILABLE (need passphrase or RSA key)")}");
        Console.WriteLine();
        Console.WriteLine("Versions:");
        foreach (var v in repo.ListVersions())
            Console.WriteLine($"  [{v.Id}] {v.Name}  {v.TimestampUtc:yyyy-MM-dd HH:mm:ss}  status={v.Status}  shares=[{string.Join(", ", v.Shares)}]");
        return 0;
    }

    private static int List(Args args)
    {
        var repo = BuildRepo(args);
        var showAll = args.Flag("all");
        var versions = args.GetInt("version") is { } vid
            ? [repo.GetVersion(vid)]
            : repo.ListVersions();

        foreach (var v in versions)
        {
            Console.WriteLine($"=== version {v.Id} ({v.Name}) ===");
            var files = repo.ListFiles(v.Id)
                .Where(f => showAll || !f.IsExtendedAttributeMeta)
                .OrderBy(f => f.ShareName, StringComparer.Ordinal)
                .ThenBy(f => f.FullPath, StringComparer.Ordinal);
            foreach (var f in files)
            {
                var kind = f.IsDirectory ? "dir " : KindLabel(f.Kind);
                var size = f.IsDirectory ? "" : HumanSize(f.Size);
                Console.WriteLine($"  {kind,-9} {size,10}  {f.ShareName}/{f.FullPath}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    private static int Restore(Args args)
    {
        var repo = BuildRepo(args);
        var version = args.GetInt("version") ?? throw new ArgumentException("--version is required");
        var fileArg = args.Require("file");
        var entry = FindFile(repo, version, fileArg);

        var restorer = new FileRestorer(repo);
        var bytes = restorer.Restore(version, entry);

        var outPath = args.Get("out") ?? Path.Combine("restored", Path.GetFileName(entry.Name));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, bytes);

        Console.WriteLine($"Restored {entry.ShareName}/{entry.FullPath}");
        Console.WriteLine($"  {bytes.Length} bytes -> {outPath}");
        Console.WriteLine($"  md5: {Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).ToLowerInvariant()}");
        if (TryPreview(bytes, out var preview))
            Console.WriteLine($"  content: {preview}");
        return 0;
    }

    private static int ArchivePlanCmd(Args args)
    {
        var repo = BuildRepo(args);
        var planner = new ArchivePlanner(repo);
        var plan = planner.Plan(args.HotVersions);

        Console.WriteLine("Archive plan (policy: keep metadata Hot, archive bulk data blobs)\n");
        if (args.HotVersions.Count > 0)
            Console.WriteLine($"Hot versions kept instantly-restorable: {string.Join(", ", args.HotVersions)}\n");

        Console.WriteLine($"{"BLOB",-40} {"SIZE",10}  {"TIER",-8} {"ACTION",-8} REASON");
        foreach (var b in plan.Blobs.Where(b => b.IsData))
            Console.WriteLine($"{Trunc(b.Path, 40),-40} {HumanSize(b.Size),10}  {b.CurrentTier,-8} {Label(b.Action),-8} {b.Reason}");

        Console.WriteLine();
        Console.WriteLine($"Metadata kept Hot : {HumanSize(plan.HotMetadataBytes)} across {plan.Blobs.Count(b => !b.IsData)} blobs");
        Console.WriteLine($"Total data        : {HumanSize(plan.DataBytes)}");
        Console.WriteLine($"Archivable        : {HumanSize(plan.ArchivableBytes)} ({Percent(plan.ArchivableBytes, plan.TotalBytes)} of repo)");

        if (args.Flag("execute"))
        {
            var moved = 0;
            foreach (var b in plan.Archivable.Where(b => b.CurrentTier != AccessTier.Archive))
            {
                repo.Storage.SetTier(b.Path, AccessTier.Archive);
                moved++;
            }
            Console.WriteLine($"\nExecuted: moved {moved} blob(s) to Archive.");
            if (repo.Storage is AzureBlobStorage)
                Console.WriteLine("(On Azure this is a real tier change; restores will require rehydration.)");
        }
        else
        {
            Console.WriteLine("\n(dry-run — pass --execute to actually move blobs to Archive)");
        }
        return 0;
    }

    private static int Rehydrate(Args args)
    {
        var repo = BuildRepo(args);
        var version = args.GetInt("version") ?? throw new ArgumentException("--version is required");
        var fileArg = args.Require("file");
        var entry = FindFile(repo, version, fileArg);

        var planner = new ArchivePlanner(repo);
        var plan = planner.PlanFileRehydration(entry);

        Console.WriteLine($"Rehydration plan for {entry.ShareName}/{entry.FullPath}\n");
        if (plan.Note is not null)
        {
            Console.WriteLine($"  cannot resolve data location: {plan.Note}");
            return 1;
        }

        foreach (var i in plan.Items)
            Console.WriteLine($"  {i.BlobPath,-40} {HumanSize(i.Size),10}  tier={i.CurrentTier,-8} {(i.NeedsRehydration ? "NEEDS REHYDRATION" : "ready")}");

        var toDo = plan.ToRehydrate.ToList();
        if (toDo.Count == 0)
        {
            Console.WriteLine("\nAll required blobs are already accessible — restore can proceed now.");
            return 0;
        }

        Console.WriteLine($"\n{toDo.Count} blob(s) must be rehydrated before restoring this file.");
        if (args.Flag("execute"))
        {
            var priority = args.Get("priority")?.Equals("high", StringComparison.OrdinalIgnoreCase) == true
                ? RehydratePriority.High : RehydratePriority.Standard;
            planner.ExecuteRehydration(plan, AccessTier.Hot, priority);
            Console.WriteLine($"Initiated rehydration to Hot (priority={priority}).");
            if (repo.Storage is AzureBlobStorage)
                Console.WriteLine("(On Azure, Standard rehydration can take up to ~15 hours; High priority is faster for small blobs.)");
            else
                Console.WriteLine("(Local simulation: rehydration is instant — the file can now be restored.)");
        }
        else
        {
            Console.WriteLine("(dry-run — pass --execute to start rehydration)");
        }
        return 0;
    }

    /// <summary>End-to-end showcase of all four features against the local sample backup.</summary>
    private static int Demo(Args args)
    {
        var repo = BuildRepo(args);
        var restorer = new FileRestorer(repo);
        var planner = new ArchivePlanner(repo);

        Rule("1) BACKUP OVERVIEW");
        Console.WriteLine($"Repository: {repo.Storage.Description}");
        Console.WriteLine($"encrypted={repo.IsEncrypted} compressed={repo.IsCompressed}");
        var version = repo.ListVersions()[0];
        Console.WriteLine($"Using version [{version.Id}] {version.Name}\n");

        Rule("2) LIST USER FILES");
        var userFiles = repo.ListFiles(version.Id)
            .Where(f => !f.IsDirectory && !f.IsExtendedAttributeMeta && f.Kind != StorageKind.VirtualFileIndex)
            .OrderBy(f => f.FullPath, StringComparer.Ordinal).ToList();
        foreach (var f in userFiles)
            Console.WriteLine($"  {KindLabel(f.Kind),-9} {HumanSize(f.Size),8}  {f.ShareName}/{f.FullPath}");

        var target = args.Get("file") is { } fa ? FindFile(repo, version.Id, fa)
            : userFiles.First(f => f.Kind == StorageKind.InlineTag);
        Console.WriteLine($"\nTarget file to restore: {target.ShareName}/{target.FullPath}");
        var loc = restorer.Locate(target);
        Console.WriteLine($"  backed by data blob(s): {string.Join(", ", loc.BlobPaths)}\n");

        Rule("3) ARCHIVE PLAN — move bulk data to Archive, keep metadata Hot");
        var plan = planner.Plan();
        foreach (var b in plan.Blobs.Where(b => b.IsData))
            Console.WriteLine($"  {Label(b.Action),-8} {HumanSize(b.Size),8}  {b.Path}");
        Console.WriteLine($"  => archivable {HumanSize(plan.ArchivableBytes)} of {HumanSize(plan.TotalBytes)} ({Percent(plan.ArchivableBytes, plan.TotalBytes)})");
        foreach (var b in plan.Archivable)
            repo.Storage.SetTier(b.Path, AccessTier.Archive);
        Console.WriteLine("  [executed] all data blobs moved to Archive.\n");

        Rule("4) RESTORE BLOCKED — chunk is in Archive");
        try
        {
            restorer.Restore(version.Id, target);
            Console.WriteLine("  (unexpected: restore succeeded though data was archived)");
        }
        catch (BlobArchivedException ex)
        {
            Console.WriteLine($"  restore failed as expected: {ex.Message}");
        }

        Rule("5) DETERMINE & REHYDRATE the right blob, then restore");
        var rplan = planner.PlanFileRehydration(target);
        foreach (var i in rplan.Items)
            Console.WriteLine($"  {i.BlobPath}  tier={i.CurrentTier}  {(i.NeedsRehydration ? "NEEDS REHYDRATION" : "ready")}");
        planner.ExecuteRehydration(rplan, AccessTier.Hot);
        Console.WriteLine("  [executed] rehydrated the required blob(s).");

        var bytes = restorer.Restore(version.Id, target);
        Console.WriteLine($"  restored {bytes.Length} bytes; md5={Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).ToLowerInvariant()}");
        if (TryPreview(bytes, out var preview))
            Console.WriteLine($"  content: {preview}");

        // cleanup: reset all data blobs to Hot so the sample is left untouched.
        foreach (var p in repo.Storage.List("").Where(ArchivePlanner.IsDataBlob))
            repo.Storage.SetTier(p, AccessTier.Hot);
        Console.WriteLine("\n  [cleanup] reset all tiers to Hot.");
        return 0;
    }

    // --- Helpers ---

    private static FileEntry FindFile(BackupRepository repo, int versionId, string fileArg)
    {
        var files = repo.ListFiles(versionId).Where(f => !f.IsDirectory).ToList();
        var matches = files.Where(f =>
            f.FullPath.Equals(fileArg, StringComparison.Ordinal) ||
            $"{f.ShareName}/{f.FullPath}".Equals(fileArg, StringComparison.Ordinal) ||
            f.FullPath.EndsWith("/" + fileArg, StringComparison.Ordinal) ||
            f.Name.Equals(fileArg, StringComparison.Ordinal)).ToList();

        if (matches.Count == 0)
            throw new ArgumentException($"No file matching '{fileArg}' in version {versionId}.");
        if (matches.Count > 1)
        {
            var exact = matches.FirstOrDefault(f => f.FullPath.Equals(fileArg, StringComparison.Ordinal)
                || $"{f.ShareName}/{f.FullPath}".Equals(fileArg, StringComparison.Ordinal));
            if (exact is not null)
                return exact;
            var candidates = string.Join("\n  ", matches.Select(m => $"{m.ShareName}/{m.FullPath}"));
            throw new ArgumentException($"Ambiguous file '{fileArg}'. Candidates:\n  {candidates}");
        }
        return matches[0];
    }

    private static bool TryPreview(byte[] bytes, out string preview)
    {
        preview = "";
        if (bytes.Length == 0 || bytes.Length > 256)
            return false;
        foreach (var b in bytes)
            if (b < 0x09 || (b > 0x0D && b < 0x20))
                return false;
        preview = '"' + Encoding.UTF8.GetString(bytes) + '"';
        return true;
    }

    private static string KindLabel(StorageKind k) => k switch
    {
        StorageKind.InlineTag => "chunk",
        StorageKind.FilePool => "filepool",
        StorageKind.VirtualFileIndex => "vindex",
        StorageKind.Empty => "empty",
        _ => "dir",
    };

    private static string Label(TierAction a) => a == TierAction.Archive ? "ARCHIVE" : "keep-hot";

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    private static string Percent(long part, long whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0.0}%";

    private static string Trunc(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];

    private static void Rule(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"────── {title} ──────");
    }

    private static void Help()
    {
        Console.WriteLine("""
            hbk — Synology HyperBackup cloud-image reader (PoC)

            USAGE
              hbk <command> [storage] [options]

            STORAGE (choose one)
              --local <dir>                          a backup folder on disk
              --azure-account <a> --azure-key <k>    Azure account + key
                 [--azure-container <c>] [--azure-prefix <p>]
              --azure-connection-string <cs> [--azure-container <c>] [--azure-prefix <p>]
              --azure-sas <containerSasUrl> [--azure-prefix <p>]
              (secrets also read from env: HBK_AZURE_KEY, HBK_AZURE_CONNECTION_STRING,
               HBK_AZURE_SAS, HBK_PASSPHRASE)

            ENCRYPTION
              --passphrase <p>      backup passphrase (for local-format encKeys.1)
              --rsa-key <file>      RSA private key exported from the NAS (cloud format)

            COMMANDS
              info                                       show format, encryption, versions
              list [--version N] [--all]                 list files (task 1)
              restore --version N --file <path> [--out f] restore a file (task 2)
              archive-plan [--hot-version N ...] [--execute]
                                                         which blobs can be archived (task 3)
              rehydrate --version N --file <path> [--execute] [--priority high]
                                                         which blob to rehydrate for a file (task 4)
              demo                                       end-to-end showcase (use with --local)

            EXAMPLES
              hbk info  --local target-azure-azure-nopassword
              hbk list  --local target-azure-azure-nopassword
              hbk restore --local target-azure-azure-nopassword --version 1 --file test.txt
              hbk demo  --local target-azure-azure-nopassword
            """);
    }
}
