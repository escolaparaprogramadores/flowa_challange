namespace Flowa.OrderGenerator.Entrypoint.DependencyInjection;

public static class HttpPortExtensions
{
    public const string DefaultHttpPort = "8080";

    extension(WebApplicationBuilder orderGeneratorBuilder)
    {
        public void UseDefaultHttpPortWhenMissing()
        {
            if (string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["HTTP_PORTS"])
                && string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["URLS"]))
                orderGeneratorBuilder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, DefaultHttpPort);
        }
    }
}
