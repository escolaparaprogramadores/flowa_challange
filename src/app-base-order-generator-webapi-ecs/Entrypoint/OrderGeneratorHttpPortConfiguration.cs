namespace Base.OrderGenerator.Entrypoint;

// Contract §4: the HTTP port comes from ASPNETCORE_HTTP_PORTS; 8080 only when nobody set a port or URL.
public static class OrderGeneratorHttpPortConfiguration
{
    public const string DefaultOrderGeneratorHttpPort = "8080";

    public static void UseDefaultOrderGeneratorHttpPortWhenMissing(WebApplicationBuilder orderGeneratorBuilder)
    {
        if (string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["HTTP_PORTS"])
            && string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["URLS"]))
            orderGeneratorBuilder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, DefaultOrderGeneratorHttpPort);
    }
}
