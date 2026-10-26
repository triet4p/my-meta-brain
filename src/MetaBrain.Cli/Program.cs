using System.Text;
using MetaBrain.Connections;

// The packaged owner CLI prints owner content (titles, bodies, notes, queries and
// previews) to the console. Windows reports the console's active code page as the
// default output encoding, so on a non-UTF-8 console the platform encoder replaced
// every unrepresentable character with '?' and redirected output was not UTF-8
// either. Publish this process's output as UTF-8 instead; attaching the console
// follows the same code page. The previous console code page is restored on exit so
// the owner's terminal is left as it was found.
var previousOutputEncoding = Console.OutputEncoding;
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
    // No usable console handle: keep the platform default and run the command.
}

try
{
    return await OwnerCliProduct.RunAsync(args).ConfigureAwait(false);
}
finally
{
    try
    {
        Console.OutputEncoding = previousOutputEncoding;
    }
    catch (IOException)
    {
        // A console that disappeared during the command needs no restoration.
    }
}
