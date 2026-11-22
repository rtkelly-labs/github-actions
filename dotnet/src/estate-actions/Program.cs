using EstateActions;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
return await Commands.RunAsync(args, cancel.Token).ConfigureAwait(false);
