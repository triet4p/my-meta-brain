using System.Security.Cryptography;
using MetaBrain.Core.Index;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace MetaBrain.Connections;

internal sealed class PinnedE5WorkspacePassageEmbeddingModel : IWorkspacePassageEmbeddingModel
{
    internal const string PinnedRevision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";
    private const int EmbeddingDimensions = 384;
    private const long OnnxModelBytes = 470_268_510;
    private const long TokenizerBytes = 17_082_730;
    private const string OnnxModelSha256 = "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665";
    private const string TokenizerSha256 = "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39";

    private Tokenizer? _tokenizer;
    private InferenceSession? _session;
    private readonly string _outputName;
    private bool _disposed;

    internal PinnedE5WorkspacePassageEmbeddingModel(string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new WorkspacePassageIndexException("workspace_index_model_missing");
        }

        string root;
        try
        {
            root = Path.GetFullPath(modelDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new WorkspacePassageIndexException("workspace_index_model_invalid");
        }

        var directory = new DirectoryInfo(root);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkspacePassageIndexException("workspace_index_model_missing");
        }

        var onnxPath = Path.Combine(root, "onnx", "model.onnx");
        var tokenizerPath = Path.Combine(root, "onnx", "tokenizer.json");
        try
        {
            RequirePinnedFile(onnxPath, OnnxModelBytes, OnnxModelSha256);
            RequirePinnedFile(tokenizerPath, TokenizerBytes, TokenizerSha256);
            _tokenizer = new Tokenizer(tokenizerPath);
            _session = new InferenceSession(onnxPath);
            _outputName = ValidateGraph(_session);
        }
        catch (WorkspacePassageIndexException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   CryptographicException or DllNotFoundException or EntryPointNotFoundException or
                                   BadImageFormatException or OnnxRuntimeException or ArgumentException)
        {
            Dispose();
            throw new WorkspacePassageIndexException("workspace_index_model_invalid");
        }
    }

    public string ModelRevision => PinnedRevision;

    public int Dimensions => EmbeddingDimensions;

    public int[] Tokenize(ReadOnlySpan<char> passage, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var input = string.Concat("passage: ", passage.ToString());
        uint[] rawIds = Array.Empty<uint>();
        int[] modelIds = Array.Empty<int>();
        try
        {
            rawIds = _tokenizer!.Encode(input).ToArray();
            if (rawIds.Length == 0)
            {
                throw new WorkspacePassageIndexException("workspace_index_tokenizer_empty");
            }

            modelIds = GC.AllocateUninitializedArray<int>(rawIds.Length);
            for (var index = 0; index < rawIds.Length; index++)
            {
                modelIds[index] = checked((int)rawIds[index]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = modelIds;
            modelIds = Array.Empty<int>();
            return result;
        }
        catch (WorkspacePassageIndexException)
        {
            throw;
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new WorkspacePassageIndexException("workspace_index_tokenizer_invalid");
        }
        finally
        {
            Array.Clear(rawIds);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(modelIds.AsSpan()));
        }
    }

    public float[] Encode(ReadOnlySpan<int> tokenIds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (tokenIds.Length is < 1 or > 512)
        {
            throw new WorkspacePassageIndexException("workspace_index_token_limit");
        }

        var inputIds = GC.AllocateUninitializedArray<long>(tokenIds.Length);
        var attentionMask = GC.AllocateUninitializedArray<long>(tokenIds.Length);
        var tokenTypeIds = GC.AllocateUninitializedArray<long>(tokenIds.Length);
        for (var index = 0; index < tokenIds.Length; index++)
        {
            if (tokenIds[index] < 0)
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(inputIds.AsSpan()));
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(attentionMask.AsSpan()));
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenTypeIds.AsSpan()));
                throw new WorkspacePassageIndexException("workspace_index_tokenizer_invalid");
            }

            inputIds[index] = tokenIds[index];
            attentionMask[index] = 1;
            tokenTypeIds[index] = 0;
        }

        float[]? vector = new float[EmbeddingDimensions];
        try
        {
            var shape = new[] { 1, tokenIds.Length };
            var inputs = new List<NamedOnnxValue>(_session!.InputMetadata.Count);
            foreach (var input in _session.InputMetadata)
            {
                var values = input.Key switch
                {
                    "input_ids" => inputIds,
                    "attention_mask" => attentionMask,
                    "token_type_ids" => tokenTypeIds,
                    _ => throw new WorkspacePassageIndexException("workspace_index_model_graph_invalid")
                };
                inputs.Add(CreateInput(input.Key, input.Value, values, shape));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var outputs = _session.Run(inputs);
            var selected = outputs.SingleOrDefault(output => string.Equals(output.Name, _outputName, StringComparison.Ordinal))
                ?? throw new WorkspacePassageIndexException("workspace_index_model_output_invalid");
            if (selected.AsTensor<float>() is not DenseTensor<float> hidden ||
                hidden.Dimensions.Length != 3 || hidden.Dimensions[0] != 1 ||
                hidden.Dimensions[1] != tokenIds.Length || hidden.Dimensions[2] != EmbeddingDimensions)
            {
                throw new WorkspacePassageIndexException("workspace_index_model_output_invalid");
            }

            var hiddenValues = hidden.Buffer.Span;
            for (var token = 0; token < tokenIds.Length; token++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceOffset = token * EmbeddingDimensions;
                for (var dimension = 0; dimension < EmbeddingDimensions; dimension++)
                {
                    vector[dimension] += hiddenValues[sourceOffset + dimension];
                }
            }

            var inverseCount = 1f / tokenIds.Length;
            double normSquared = 0;
            for (var dimension = 0; dimension < EmbeddingDimensions; dimension++)
            {
                vector[dimension] *= inverseCount;
                normSquared += (double)vector[dimension] * vector[dimension];
            }

            if (!double.IsFinite(normSquared) || normSquared <= 0)
            {
                throw new WorkspacePassageIndexException("workspace_index_model_output_invalid");
            }

            var inverseNorm = (float)(1d / Math.Sqrt(normSquared));
            for (var dimension = 0; dimension < EmbeddingDimensions; dimension++)
            {
                vector[dimension] *= inverseNorm;
                if (!float.IsFinite(vector[dimension]))
                {
                    throw new WorkspacePassageIndexException("workspace_index_model_output_invalid");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = vector;
            vector = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(inputIds.AsSpan()));
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(attentionMask.AsSpan()));
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenTypeIds.AsSpan()));
            if (vector is not null)
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose();
        _session = null;
        _tokenizer?.Dispose();
        _tokenizer = null;
        GC.SuppressFinalize(this);
    }

    private static NamedOnnxValue CreateInput(string name, NodeMetadata metadata, long[] values, int[] shape)
    {
        if (metadata.Dimensions.Length != 2 ||
            (metadata.Dimensions[0] >= 0 && metadata.Dimensions[0] != shape[0]) ||
            (metadata.Dimensions[1] >= 0 && metadata.Dimensions[1] != shape[1]) ||
            metadata.ElementType != typeof(long))
        {
            throw new WorkspacePassageIndexException("workspace_index_model_graph_invalid");
        }

        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, shape));
    }

    private static string ValidateGraph(InferenceSession session)
    {
        var inputNames = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        if (!inputNames.Contains("input_ids") || !inputNames.Contains("attention_mask") ||
            inputNames.Any(name => name is not ("input_ids" or "attention_mask" or "token_type_ids")))
        {
            throw new WorkspacePassageIndexException("workspace_index_model_graph_invalid");
        }

        foreach (var input in session.InputMetadata.Values)
        {
            if (input.Dimensions.Length != 2 || input.ElementType != typeof(long))
            {
                throw new WorkspacePassageIndexException("workspace_index_model_graph_invalid");
            }
        }

        var output = session.OutputMetadata.SingleOrDefault(item =>
            item.Value.Dimensions.Length == 3 && item.Value.ElementType == typeof(float) &&
            (item.Value.Dimensions[2] < 0 || item.Value.Dimensions[2] == EmbeddingDimensions));
        if (output.Key is null)
        {
            throw new WorkspacePassageIndexException("workspace_index_model_graph_invalid");
        }

        return output.Key;
    }

    private static void RequirePinnedFile(string path, long expectedLength, string expectedSha256)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new WorkspacePassageIndexException("workspace_index_model_missing");
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != expectedLength)
        {
            throw new WorkspacePassageIndexException("workspace_index_model_invalid");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(digest, expectedSha256, StringComparison.Ordinal))
        {
            throw new WorkspacePassageIndexException("workspace_index_model_invalid");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
