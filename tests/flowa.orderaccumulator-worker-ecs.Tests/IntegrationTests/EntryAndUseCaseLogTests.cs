using System.Diagnostics;

namespace Flowa.OrderAccumulator.Tests;

// CA-21 and CA-28: one Information line where something enters the app (start) and none from the framework,
// read from the real stdout. The worker has no HTTP, so no request line exists; the FIX entry lines are in OrderDecisionLogTests.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class EntryAndUseCaseLogTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string ProgramCategory = "Program";
    private const string FixSessionLogCategory = "Flowa.OrderAccumulator.Infrastructure.Fix.FixSessionLog";

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Start_writes_only_the_application_started_line_with_the_commit_of_the_build()
    {
        // Arrange
        var gitHeadSha = ReadGitHeadSha();

        // Act
        var appLogLines = await RunAppAndReadAppLogLinesAsync();

        // Assert
        Assert.Matches("^[0-9a-f]{40}$", gitHeadSha);
        var applicationStartedLine = Assert.Single(appLogLines, appLogLine => appLogLine.Category == ProgramCategory);
        Assert.Equal(("Information", "Application started."), (applicationStartedLine.LogLevel, applicationStartedLine.Message));
        Assert.Equal((gitHeadSha, "Development"), (applicationStartedLine.ReadLogField("BuildCommitSha"), applicationStartedLine.ReadLogField("Environment")));
        Assert.Single(appLogLines);
    }

    // The lines the application writes, except the QuickFIX session events, which were already there in c41ed4b and stay in
    // the FixSessionLog. No ASP.NET Core category writes a line at all: the worker has no web host.
    private async Task<IReadOnlyList<JsonLogLine>> RunAppAndReadAppLogLinesAsync()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        await using (await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync())
        {
        }

        var stdoutLogLines = stdoutJsonLogCapture.JsonLogLines;
        Assert.DoesNotContain(stdoutLogLines, stdoutLogLine => stdoutLogLine.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        return stdoutLogLines
            .Where(stdoutLogLine => stdoutLogLine.Category == ProgramCategory || stdoutLogLine.Category.StartsWith("Flowa.OrderAccumulator.", StringComparison.Ordinal))
            .Where(stdoutLogLine => stdoutLogLine.Category != FixSessionLogCategory)
            .ToList();
    }

    // Oracle from outside the app: the git of the repository where the tests were built.
    private static string ReadGitHeadSha()
    {
        using var gitRevParseProcess = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory
        })!;
        var gitHeadSha = gitRevParseProcess.StandardOutput.ReadToEnd().Trim();
        gitRevParseProcess.WaitForExit();
        return gitHeadSha;
    }
}
