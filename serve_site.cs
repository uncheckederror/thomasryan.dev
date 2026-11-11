#:sdk Microsoft.NET.Sdk.Web
using Microsoft.Extensions.FileProviders;
using System.Runtime.CompilerServices;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
var files = new PhysicalFileProvider(SourceDirectory());
app.Urls.Add("http://localhost:8000");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
app.Run();

static string SourceDirectory([CallerFilePath] string source = "") =>
    Path.GetDirectoryName(source)!;
