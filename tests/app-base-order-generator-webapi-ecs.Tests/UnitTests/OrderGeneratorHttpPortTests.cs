using Base.OrderGenerator.Entrypoint;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Base.OrderGenerator.Tests;

// Contract §4: the HTTP port comes from ASPNETCORE_HTTP_PORTS; 8080 only when nobody set one.
public sealed class OrderGeneratorHttpPortTests
{
    [Fact]
    public void Http_port_comes_from_the_contract_variable_and_defaults_to_8080_only_when_missing()
    {
        var originalAspNetCoreHttpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var originalAspNetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);

            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "18080");
            Assert.Equal("18080", ReadHttpPortsChosenAtOrderGeneratorStartup());

            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", null);
            Assert.Equal(OrderGeneratorHttpPortConfiguration.DefaultOrderGeneratorHttpPort, ReadHttpPortsChosenAtOrderGeneratorStartup());

            // With ASPNETCORE_URLS set, it decides: the default 8080 must not override it.
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:18081");
            Assert.Null(ReadHttpPortsChosenAtOrderGeneratorStartup());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", originalAspNetCoreHttpPorts);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalAspNetCoreUrls);
        }
    }

    // Same root as Program: reads the appsettings.json that ships next to the binary.
    private static string? ReadHttpPortsChosenAtOrderGeneratorStartup()
    {
        var orderGeneratorStartupBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        OrderGeneratorHttpPortConfiguration.UseDefaultOrderGeneratorHttpPortWhenMissing(orderGeneratorStartupBuilder);
        return orderGeneratorStartupBuilder.WebHost.GetSetting(WebHostDefaults.HttpPortsKey);
    }
}
