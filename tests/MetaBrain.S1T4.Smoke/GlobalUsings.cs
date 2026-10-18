// Restores the console implicit usings that the WPF SDK omits: the smoke suite
// touches the file system throughout, and only the S6-T5 render helper
// touches presentation types.
global using System.IO;
global using System.Runtime.ExceptionServices;
