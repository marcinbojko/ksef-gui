using System.Text.Json;
using CommandLine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KSeFCli;

[Verb("PrintConfig", HelpText = "Print the active configuration")]
public class PrintConfigCommand : IWithConfigCommand
{
    [Option("json", HelpText = "Output configuration in JSON format")]
    public bool JsonOutput { get; set; }

    public override Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();

        if (JsonOutput)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            Console.WriteLine(json);
        }
        else
        {
            ISerializer serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(config);
            Console.WriteLine(yaml);
        }

        return Task.FromResult(0);
    }
}
