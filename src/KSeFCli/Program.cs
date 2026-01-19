using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.CommandLine;

using System;
using System.Collections.Generic;
using CommandLine;

namespace KSeFCli {
class Program {
  class GlobalOptions {
    [Option("token", Default = null, HelpText = "KSeF API token")]
    public string Token { get; set; } =
        Environment.GetEnvironmentVariable("KSEF_TOKEN") ??
        throw new InvalidOperationException("KSEF_TOKEN not set");

    [Option("base-url", Default = null, HelpText = "KSeF base URL")]
    public string BaseUrl { get; set; } =
        Environment.GetEnvironmentVariable("KSEF_URL") ??
        throw new InvalidOperationException("KSEF_URL not set");
  }

  [Verb("get-invoice", HelpText = "Get a single invoice by KSeF number")]
  class GetInvoiceOptions : GlobalOptions {
    [Option('k', "ksef-number", Required = true,
            HelpText = "KSeF invoice number")]
    public string KsefNumber { get; set; } = null!;
  }

  [Verb("query-metadata", HelpText = "Query invoice metadata")]
  class QueryMetadataOptions : GlobalOptions {
    [Option('s', "subject-type", Required = true,
            HelpText = "Invoice subject type")]
    public string SubjectType { get; set; } = null!;
  }

  static int Main(string[] args) {
    return Parser.Default
        .ParseArguments<GetInvoiceOptions, QueryMetadataOptions>(args)
        .MapResult((GetInvoiceOptions opts) => RunGetInvoice(opts),
                   (QueryMetadataOptions opts) => RunQueryMetadata(opts),
                   errs => 1);
  }

  static int RunGetInvoice(GetInvoiceOptions opts) {
    Console.WriteLine($"Using token: {opts.Token}");
    Console.WriteLine($"Using base URL: {opts.BaseUrl}");
    Console.WriteLine($"Fetching invoice: {opts.KsefNumber}");

    // Call KSeF.Client here, e.g.
    // var client = new KSeFClient(opts.BaseUrl);
    // var invoice = client.GetInvoiceAsync(opts.KsefNumber, opts.Token);

    return 0;
  }

  static int RunQueryMetadata(QueryMetadataOptions opts) {
    Console.WriteLine($"Using token: {opts.Token}");
    Console.WriteLine($"Using base URL: {opts.BaseUrl}");
    Console.WriteLine($"Querying metadata for subject: {opts.SubjectType}");

    // Call KSeF.Client here for metadata query

    return 0;
  }
}
}
