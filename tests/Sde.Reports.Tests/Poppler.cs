using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sde.Reports.Tests;

/// <summary>Reads rendered PDFs back with poppler's command-line tools (brew install poppler).</summary>
internal static class Poppler
{
	private static string Tool(string name)
	{
		foreach (string dir in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" })
		{
			string path = Path.Combine(dir, name);
			if (File.Exists(path))
			{
				return path;
			}
		}
		throw new InvalidOperationException($"{name} not found; install poppler (brew install poppler).");
	}

	private static string Run(string tool, params string[] args)
	{
		ProcessStartInfo psi = new ProcessStartInfo(Tool(tool))
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi)!;
		Task<string> err = p.StandardError.ReadToEndAsync();
		string output = p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		if (p.ExitCode != 0)
		{
			throw new InvalidOperationException($"{tool} failed ({p.ExitCode}): {err.Result}");
		}
		return output;
	}

	/// <summary>pdftotext -layout (optionally one page), with runs of spaces collapsed so assertions are layout-independent.</summary>
	public static string Text(string pdf, int? page = null)
	{
		List<string> args = new() { "-layout", "-enc", "UTF-8" };
		if (page is int n)
		{
			args.AddRange(new[] { "-f", n.ToString(), "-l", n.ToString() });
		}
		args.AddRange(new[] { pdf, "-" });
		return Regex.Replace(Run("pdftotext", args.ToArray()), "[ \t]+", " ");
	}

	public static int Pages(string pdf)
	{
		Match m = Regex.Match(Run("pdfinfo", pdf), @"^Pages:\s+(\d+)", RegexOptions.Multiline);
		return int.Parse(m.Groups[1].Value);
	}

	/// <summary>Embedded images as (page, width, height), from pdfimages -list.</summary>
	public static List<(int Page, int Width, int Height)> Images(string pdf)
	{
		List<(int, int, int)> list = new();
		foreach (string line in Run("pdfimages", "-list", pdf).Split('\n').Skip(2))
		{
			string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length > 5 && parts[2] == "image")
			{
				list.Add((int.Parse(parts[0]), int.Parse(parts[3]), int.Parse(parts[4])));
			}
		}
		return list;
	}

	/// <summary>Renders one page to PNG (pdftoppm) and returns the PNG path.</summary>
	public static string PagePng(string pdf, int page, int dpi, string outPrefix)
	{
		Run("pdftoppm", "-png", "-r", dpi.ToString(), "-f", page.ToString(), "-l", page.ToString(), "-singlefile", pdf, outPrefix);
		return outPrefix + ".png";
	}
}
