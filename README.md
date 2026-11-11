# SqlClient CPU per query: .NET 10 vs .NET 11

One small console app, built for `net10.0` and `net11.0` from the same source, measuring the process's CPU time per
Microsoft.Data.SqlClient query. On .NET 11 each query costs noticeably more CPU, with the same SqlClient binary and the
same throughput.

Each query opens a pooled connection, runs `SELECT ... WHERE Id = @Id` against a 100,000-row table, reads the row and
closes the connection. N workers do this in a closed loop. After a 5 s warm-up, the app reports queries per second,
process CPU µs per query (`Process.TotalProcessorTime` over the measured window) and bytes allocated per query.

## Run it

You need Docker, the .NET 10 and .NET 11 SDKs, and PowerShell 7. The runner supports Windows and Linux.
On Linux it also needs `taskset` (provided by `util-linux` on Debian/Ubuntu).

1. Start a SQL Server:

   ```bash
   docker run -d --name sqlclient-repro -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Repro-Passw0rd!" -p 14339:1433 mcr.microsoft.com/mssql/server:2022-latest
   ```

   Wait until the container logs say SQL Server is ready for client connections:

   ```bash
   docker logs --tail 20 sqlclient-repro
   ```

   On a Linux host using Docker's default bridge network, if the published port gives pre-login handshake timeouts,
   connect directly to the container instead. Run this in the same shell before setup and the benchmark:

   ```bash
   sql_ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' sqlclient-repro)
   export REPRO_CONNECTION_STRING="Server=$sql_ip,1433;Database=ReproDb;User Id=sa;Password=Repro-Passw0rd!;TrustServerCertificate=True;"
   ```

   The direct address can change when the container is recreated. This workaround is for native Linux Docker,
   not Docker Desktop or a remote Docker host.

2. Create the table (once):

   ```bash
   dotnet run -c Release -f net11.0 -- --setup
   ```

3. Run both runtimes alternately, pinned to the same 8 logical CPUs:

   ```bash
   pwsh ./run.ps1
   ```

   - `-SqlClientVersion 8.0.0-preview1.26313.4` uses another SqlClient version
   - `-ManagedSni` switches SqlClient to its managed networking on Windows (`Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows`); Linux always uses managed networking
   - `-Mars` adds `MultipleActiveResultSets=True`
   - `-Affinity` sets the CPU mask (default `0xFF`; on a hybrid Intel CPU keep it on performance cores)
   - `-NoRestore` skips package restore when the selected SqlClient version has already been restored

   If NuGet is unreachable but packages are already restored, use `-NoRestore` (and `--no-restore` on the setup command).

   The runner restores the selected package version, builds both runtimes into version-specific directories, and runs
   those exact builds. On Linux, `taskset` pins each child before .NET starts. Check your allowed CPUs with
   `taskset -pc $$` and choose an allowed mask if CPUs 0-7 are unavailable (for example, `-Affinity 0xFF00` for CPUs 8-15).

   For a quick Linux smoke test (one round, four workers, one measured second per runtime after warm-up):

   ```bash
   pwsh ./run.ps1 -Rounds 1 -Concurrency 4 -Seconds 1
   ```

   For another server, set `REPRO_CONNECTION_STRING`; the database it names is created by `--setup`.

4. Remove the container:

   ```bash
   docker rm -f sqlclient-repro
   ```

Each line of output is one 15-second measurement:

```
.NET 10.0.14 SqlClient 7.1.1 (native SNI)  procs 8  c=16     20328 queries/s     76.7 us CPU/query     5191 B/query  errors 0
.NET 11.0.0  SqlClient 7.1.1 (native SNI)  procs 8  c=16     19974 queries/s     89.9 us CPU/query     5183 B/query  errors 0
```

`ISSUE.md` is the write-up for dotnet/runtime, with the original Windows results; these are not Linux measurements.
The installed runtime patch versions may differ, so use the versions printed by each run when comparing results.
