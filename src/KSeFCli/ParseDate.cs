using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace KSeFCli;

public static class ParseDate
{
    public static async Task<DateTime> Parse(string dateString)
    {
        // Try parsing using standard C# DateTime.Parse
        if (DateTime.TryParse(dateString, out DateTime result))
        {
            return result;
        }

        // Try parsing with specific formats if standard parsing fails
        string[] formats = {
            "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm:ss",
            "dd-MM-yyyy",
            "dd-MM-yyyy HH:mm:ss",
            "yyyy/MM/dd",
            "yyyy/MM/dd HH:mm:ss"
        };
        if (DateTime.TryParseExact(dateString, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return result;
        }

        // If C# parsing fails, try using the 'date' shell command for relative dates
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "date",
                Arguments = $@"-d ""{dateString}"" +%s",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = Process.Start(startInfo);
            await process.WaitForExitAsync();

            string output = await process.StandardOutput.ReadToEndAsync();
            if (long.TryParse(output.Trim(), out long unixTimestamp))
            {
                DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
                return dateTimeOffset.LocalDateTime; // Convert to local time
            }
        }
        catch (Exception ex)
        {
            // Log or handle the exception if shell command execution fails
            Console.Error.WriteLine($"Error parsing date with shell command: {ex.Message}");
        }

        throw new FormatException($"Could not parse date string: {dateString}");
    }
}
