using System.Text.Json;

using CommandLine;

using Microsoft.Extensions.DependencyInjection;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KSeFCli;

[Verb("PrintConfig", HelpText = "Print the active configuration")]
public class PrintConfigCommand : IWithConfigCommand
{
    [Option("json", HelpText = "Output configuration in JSON format")]
    public bool JsonOutput { get; set; }

    public override Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        ProfileConfig config = Config();

        if (JsonOutput)
        {
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, options);
            Console.WriteLine(json);
        }
        else
        {
            ISerializer serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            string yaml = serializer.Serialize(config);
            Console.WriteLine(yaml);
        }

        return Task.FromResult(0);
    }
}
