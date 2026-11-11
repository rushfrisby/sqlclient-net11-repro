using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

// Closed loop of N concurrent workers, each doing: open a pooled connection, SELECT one row by primary key, read it, close.
// Reports process CPU time per query, which is what regresses on .NET 11; throughput is limited by the server round trip.
//
//   SqlClientCpuRepro --setup                     create and fill the table (once)
//   SqlClientCpuRepro [concurrency] [seconds]     default 16 workers, 5 s warm-up then 15 s measured
//
// Connection string: environment variable REPRO_CONNECTION_STRING.
// REPRO_MANAGED_SNI=1 switches SqlClient to its managed networking on Windows.
// REPRO_MARS=1 adds MultipleActiveResultSets=True.

var connectionString = Environment.GetEnvironmentVariable("REPRO_CONNECTION_STRING")
    ?? "Server=127.0.0.1,14339;Database=ReproDb;User Id=sa;Password=Repro-Passw0rd!;TrustServerCertificate=True;";
if (Environment.GetEnvironmentVariable("REPRO_MARS") == "1")
{
    connectionString += ";MultipleActiveResultSets=True";
}
if (Environment.GetEnvironmentVariable("REPRO_MANAGED_SNI") == "1")
{
    AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);
}

const int Rows = 100_000;

if (args.Length > 0 && args[0] == "--setup")
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    var database = builder.InitialCatalog;
    builder.InitialCatalog = "master";
    await using (var master = new SqlConnection(builder.ConnectionString))
    {
        await master.OpenAsync();
        await using var create = new SqlCommand($"IF DB_ID('{database}') IS NULL CREATE DATABASE [{database}]", master);
        await create.ExecuteNonQueryAsync();
    }
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var fill = new SqlCommand($"""
        IF OBJECT_ID('dbo.Item') IS NULL
        BEGIN
            CREATE TABLE dbo.Item (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Email VARCHAR(128) NOT NULL, CreatedOn DATETIME2 NOT NULL);
            INSERT dbo.Item (Id, Name, Email, CreatedOn)
            SELECT TOP ({Rows}) n, CONCAT(N'Name ', n), CONCAT('user', n, '@example.com'), SYSUTCDATETIME()
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;
        END
        """, connection);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
    Console.WriteLine("setup done");
    return;
}

var concurrency = args.Length > 0 ? int.Parse(args[0]) : 16;
var measure = TimeSpan.FromSeconds(args.Length > 1 ? double.Parse(args[1]) : 15);
var warmup = TimeSpan.FromSeconds(5);

async Task QueryAsync(Random rng)
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new SqlCommand("SELECT Id, Name, Email, CreatedOn FROM dbo.Item WHERE Id = @Id", connection);
    command.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = rng.Next(Rows) });
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        _ = reader.GetInt32(0);
        _ = reader.GetString(1);
        _ = reader.GetString(2);
        _ = reader.GetDateTime(3);
    }
}

var stop = false;
var measuring = false;
long ops = 0;
long errors = 0;
var workers = Enumerable.Range(0, concurrency).Select(w => Task.Run(async () =>
{
    var rng = new Random(w + 1);
    while (!Volatile.Read(ref stop))
    {
        try
        {
            await QueryAsync(rng);
            if (Volatile.Read(ref measuring))
            {
                Interlocked.Increment(ref ops);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref errors) == 1)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }
    }
})).ToArray();

await Task.Delay(warmup);
var process = Process.GetCurrentProcess();
process.Refresh();
var cpu0 = process.TotalProcessorTime;
var alloc0 = GC.GetTotalAllocatedBytes(true);
var sw = Stopwatch.StartNew();
Volatile.Write(ref measuring, true);
await Task.Delay(measure);
Volatile.Write(ref measuring, false);
var seconds = sw.Elapsed.TotalSeconds;
process.Refresh();
var cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
var alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
Volatile.Write(ref stop, true);
await Task.WhenAll(workers);

var n = Interlocked.Read(ref ops);
if (n == 0)
{
    throw new InvalidOperationException("No queries completed during the measurement. Check the connection string and run --setup first.");
}
var sqlClient = typeof(SqlConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
var sni = !OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("REPRO_MANAGED_SNI") == "1" ? "managed" : "native";
Console.WriteLine($"{RuntimeInformation.FrameworkDescription,-12} SqlClient {sqlClient} ({sni} SNI)  procs {Environment.ProcessorCount}  c={concurrency}  " +
                  $"{n / seconds,8:F0} queries/s  {cpuMs * 1000 / n,7:F1} us CPU/query  {(double)alloc / n,7:F0} B/query  errors {Interlocked.Read(ref errors)}");
Environment.ExitCode = Interlocked.Read(ref errors) == 0 ? 0 : 1;
