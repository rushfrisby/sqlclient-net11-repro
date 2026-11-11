### Description

After moving an ASP.NET Core service from .NET 10 to .NET 11, each API request costs 4–24% more process CPU at the same
throughput and latency. Measuring the request path layer by layer put almost all of it in Microsoft.Data.SqlClient. The
ASP.NET Core pipeline itself got cheaper on .NET 11 (9–28% less CPU per request), and JWT validation costs the same.

The repro below has no other dependencies. It runs one query loop, built from the same source for `net10.0` and
`net11.0`. Both builds load the same SqlClient assembly (7.1.1's `net9.0` build), and the only thing that changes is
the runtime. Queries per second and bytes allocated per query are the same on both. Only CPU time per query goes up.

### Reproduction Steps

Full project (csproj, Program.cs, run.ps1, README): https://github.com/rushfrisby/sqlclient-net11-repro

Each of N workers runs this in a closed loop:

```csharp
await using var connection = new SqlConnection(connectionString);   // pooled
await connection.OpenAsync();
await using var command = new SqlCommand("SELECT Id, Name, Email, CreatedOn FROM dbo.Item WHERE Id = @Id", connection);
command.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = rng.Next(100_000) });
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync()) { /* read the four columns */ }
```

After a 5 s warm-up it measures for 15 s: queries per second, `Process.TotalProcessorTime` divided by queries, and
`GC.GetTotalAllocatedBytes` divided by queries.

1. `docker run -d --name sqlclient-repro -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Repro-Passw0rd!" -p 14339:1433 mcr.microsoft.com/mssql/server:2022-latest`
2. `dotnet run -c Release -f net11.0 -- --setup` (creates a 100,000-row table)
3. `pwsh ./run.ps1`, which builds both TFMs and runs them alternately. Each process is pinned to the same 8 logical CPUs
   (4 performance cores with hyperthreading) through an inherited affinity mask, so `Environment.ProcessorCount` is 8
   from startup on both. Options: `-SqlClientVersion`, `-ManagedSni`, `-Mars`.

### Expected behavior

CPU time per query about the same on .NET 11 as on .NET 10.

### Actual behavior

Process CPU µs per query, mean of alternating 15 s runs, with min–max in brackets:

| SqlClient | Networking | Workers | .NET 10.0.14 | .NET 11.0.0 | Change | Runs |
|---|---|---|---|---|---|---|
| 7.1.1 | native SNI (default) | 4 | 79.7 [75.8–86.7] | 108.3 [97.1–121.5] | **+36%** | 3 + 3 |
| 7.1.1 | native SNI (default) | 16 | 67.6 [62.7–70.3] | 82.8 [78.3–88.9] | **+22%** | 3 + 3 |
| 8.0.0-preview1.26313.4 | native SNI | 4 | 91.9 [86.8–97.0] | 102.2 [91.4–112.9] | +11% | 2 + 2 |
| 8.0.0-preview1.26313.4 | native SNI | 16 | 68.8 [68.0–69.5] | 84.8 [84.6–85.0] | **+23%** | 2 + 2 |
| 7.1.1 | managed (`UseManagedNetworkingOnWindows`) | 4 | 93.0 [86.9–99.1] | 97.3 [94.5–100.0] | +5% | 2 + 2 |
| 7.1.1 | managed (`UseManagedNetworkingOnWindows`) | 16 | 73.5 [70.1–76.8] | 91.1 [90.8–91.3] | **+24%** | 2 + 2 |

On both runtimes, throughput is about 20,500 queries/s at 16 workers and about 7,400 at 4, because the loop is bound
by the round trip. Allocations are 5,191 B per query on .NET 10 and 5,183 B on .NET 11 (native SNI); managed SNI is
5,890 vs 5,888 B.

At 16 workers, where the measurements are steadiest, the .NET 11 and .NET 10 ranges don't overlap in any configuration.

### Regression?

Yes, compared with .NET 10.0.14, the only .NET 10 version I tested.

### Known Workarounds

None found:

- Managed SNI uses about the same or more CPU on both runtimes, and still regresses at 16 workers.
- SqlClient 8.0.0-preview1 (the `net10.0` build with the new native SNI) behaves like 7.1.1.
- In the full service, turning off thread pool spinning on both runtimes (`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`)
  lowered CPU on both, but .NET 11 still cost 6–20% more per request. So the extra time isn't spin-waiting.

### Configuration

- .NET SDK 11.0.100 (runtime 11.0.0) and .NET SDK 10.0.403 (runtime 10.0.14), x64
- Windows 11 Pro 10.0.26300, Intel Core i9-12900KF, 64 GB
- Server GC (the default for the repro and for the ASP.NET Core service), default GC settings (DATAS)
- SQL Server 2022 CU27 in Docker 28.3.3 (Docker Desktop, WSL 2) on the same machine, TCP on loopback
- Microsoft.Data.SqlClient 7.1.1 and 8.0.0-preview1.26313.4

The results above were collected on Windows. The repo's runner also supports Linux via `taskset`; Linux always uses
managed networking, so the native SNI results above do not apply there. See the README for Linux run instructions.

### Other information

- **Not the SqlClient version:** the same `net9.0` SqlClient 7.1.1 assembly loads on both runtimes, and 8.0.0-preview1
  shows the same pattern.
- **Not GC:** allocations per query are identical. In the full service, time in GC and GC counts were also about the
  same.
- **Not ASP.NET Core:** in the full service, the ASP.NET Core pipeline on TestServer, with the data layer stubbed out,
  was cheaper per request on .NET 11.
- **Broader than native SNI:** native SNI completes I/O through the thread pool's I/O completion port, while managed SNI
  uses `Socket` async I/O. Both regress at 16 workers, so the cost may be in async I/O completion or continuation
  dispatch generally, rather than in one of the two.
- **Possibly related:** [Performance Improvements in .NET 11](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/)
  describes a change to how overlapped I/O completions are signaled on Windows. I haven't been able to connect it to this.
- **Not profiled at the OS level:** I couldn't take an ETW CPU profile (that needs an elevated session). The EventPipe
  sample profiler attributes busy samples differently on the two runtimes (.NET 10 reports `PollGC` and safepoint frames,
  .NET 11 the actual leaf frames), so those profiles don't compare. I'm happy to collect a trace if you tell me what
  would help.
