using MetaBrain.Connections;

// The packaged owner CLI: help/version plus the existing shared lifecycle and
// owner commands. It never starts a second implementation and never reaches a
// vault file directly.
return await OwnerCliProduct.RunAsync(args).ConfigureAwait(false);
