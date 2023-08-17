using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using HotChocolate.Execution;
using HotChocolate.Execution.Processing;
using HotChocolate.Language;
using static HotChocolate.Stitching.WellKnownContextData;

namespace HotChocolate.Stitching.Requests;

internal static class MergeRequestHelper
{
    public static IEnumerable<(IQueryRequest, IEnumerable<BufferedRequest>)> MergeRequests(
        IEnumerable<BufferedRequest> requests)
    {
        foreach (var group in requests.GroupBy(t => t.Operation.Operation))
        {
            var rewriter = new MergeRequestRewriter();
            var variableValues = new Dictionary<string, object?>();

            var operationName = group
                .Select(r => r.Request.OperationName)
                .Where(n => n != null)
                .Distinct()
                .FirstOrDefault();

            if (operationName is not null)
            {
                rewriter.SetOperationName(new NameNode(operationName));
            }

            var i = 0;
            BufferedRequest first = null!;
            foreach (var request in group)
            {
                first ??= request;
                MergeRequest(request, rewriter, variableValues, $"__{i++}_");
            }

            var batch =
                QueryRequestBuilder.New()
                    .SetQuery(rewriter.Merge())
                    .SetOperation(operationName)
                    .SetVariableValues(variableValues)
                    .TrySetServices(first.Request.Services)
                    .Create();

            yield return (batch, group);
        }
    }

    public static void DispatchResults(
        IQueryResult mergedResult,
        IEnumerable<BufferedRequest> requests)
    {
        try
        {
            var handledErrors = new HashSet<IError>();
            BufferedRequest? current = null;
            QueryResultBuilder? resultBuilder = null;

            foreach (var request in requests)
            {
                if (current is not null && resultBuilder is not null)
                {
                    current.Promise.SetResult(resultBuilder.Create());
                }

                try
                {
                    current = request;
                    resultBuilder = ExtractResult(request.Aliases!, mergedResult, handledErrors);
                }
                catch (Exception ex)
                {
                    current = null;
                    resultBuilder = null;
                    request.Promise.SetException(ex);
                }
            }

            if (current is not null && resultBuilder is not null)
            {
                if (mergedResult.Errors is not null &&
                    handledErrors.Count < mergedResult.Errors.Count)
                {
                    foreach (var error in mergedResult.Errors.Except(handledErrors))
                    {
                        resultBuilder.AddError(error);
                    }
                }

                handledErrors.Clear();
                current.Promise.SetResult(resultBuilder.Create());
            }
        }
        catch (Exception ex)
        {
            foreach (var request in requests)
            {
                request.Promise.TrySetException(ex);
            }
        }
    }

    private static void MergeRequest(
        BufferedRequest bufferedRequest,
        MergeRequestRewriter rewriter,
        IDictionary<string, object?> variableValues,
        string requestPrefix)
    {
        MergeVariables(
            bufferedRequest.Request.VariableValues,
            variableValues,
            requestPrefix);

        var isAutoGenerated =
            bufferedRequest.Request.ContextData?.ContainsKey(IsAutoGenerated) ?? false;

        bufferedRequest.Aliases = rewriter.AddQuery(
            bufferedRequest,
            requestPrefix,
            isAutoGenerated);
    }

    private static void MergeVariables(
        IReadOnlyDictionary<string, object?>? original,
        IDictionary<string, object?> merged,
        string requestPrefix)
    {
        if (original is not null)
        {
            foreach (var item in original)
            {
                var variableName = item.Key.CreateNewName(requestPrefix);
                merged.Add(variableName, item.Value);
            }
        }
    }

    // This method extracts the relevant data from a merged result for a specific result.
    private static QueryResultBuilder ExtractResult(
        IDictionary<string, string> aliases,
        IQueryResult mergedResult,
        ICollection<IError> handledErrors)
    {
        var result = QueryResultBuilder.New();

        // We first try to identify and copy data segments that belong to our specific result.
        ExtractData(aliases, mergedResult, result);

        // After extracting the data, we will try to find errors that can be associated with
        // our specific request for which we are trying to branch out the result.
        ExtractErrors(aliases, mergedResult, handledErrors, result);

        // Last but not least we will copy all extensions and contextData over
        // to the specific responses.
        if (mergedResult.Extensions is not null)
        {
            result.SetExtensions(mergedResult.Extensions);
        }

        if (mergedResult.ContextData is not null)
        {
            foreach (var item in mergedResult.ContextData)
            {
                result.SetContextData(item.Key, item.Value);
            }
        }

        return result;
    }

    private static void ExtractData(
        IDictionary<string, string> aliases,
        IQueryResult mergedResult,
        QueryResultBuilder result)
    {
        var data = new ObjectResult();
        data.EnsureCapacity(aliases.Count);
        var i = 0;

        if (mergedResult.Data is not null)
        {
            foreach (var alias in aliases)
            {
                if (mergedResult.Data.TryGetValue(alias.Key, out var o))
                {
                    data.SetValueUnsafe(i++, alias.Value, o);
                }
            }
        }
        else
        {
            foreach (var alias in aliases)
            {
                data.SetValueUnsafe(i++, alias.Value, null);
            }
        }

        result.SetData(data);
    }

    private static void ExtractErrors(
        IDictionary<string, string> aliases,
        IQueryResult mergedResult,
        ICollection<IError> handledErrors,
        QueryResultBuilder result)
    {
        if (mergedResult.Errors is not null)
        {
            foreach (var error in mergedResult.Errors)
            {
                if (TryResolveField(error, aliases, out var responseName))
                {
                    handledErrors.Add(error);
                    result.AddError(RewriteError(error, responseName));
                }
            }
        }
    }

    private static IError RewriteError(IError error, string responseName)
    {
        if (error.Path is null)
        {
            return error;
        }

        return error.WithPath(error.Path.Length == 1
            ? Path.Root.Append(responseName)
            : ReplaceRoot(error.Path, responseName));
    }

    private static bool TryResolveField(
        IError error,
        IDictionary<string, string> aliases,
        [NotNullWhen(true)] out string? responseName)
    {
        if (GetRoot(error.Path) is NamePathSegment root &&
            aliases.TryGetValue(root.Name, out var s))
        {
            responseName = s;
            return true;
        }

        responseName = null;
        return false;
    }

    private static Path? GetRoot(Path? path)
    {
        var current = path;

        if (current is null || current.IsRoot)
        {
            return null;
        }

        while (!current.Parent.IsRoot)
        {
            current = current.Parent;
        }

        return current;
    }

    private static Path ReplaceRoot(Path path, string responseName)
    {
        var depth = path.Length;
        var buffer = ArrayPool<Path>.Shared.Rent(depth);
        var paths = buffer.AsSpan().Slice(0, depth);

        try
        {
            var current = path;

            do
            {
                paths[--depth] = current;
                current = current.Parent;
            } while (!current.IsRoot);

            paths = paths.Slice(1);

            current = Path.Root.Append(responseName);

            for (var i = 0; i < paths.Length; i++)
            {
                if (paths[i] is IndexerPathSegment index)
                {
                    current = current.Append(index.Index);
                }
                else if (paths[i] is NamePathSegment name)
                {
                    current = current.Append(name.Name);
                }
            }

            return current;
        }
        finally
        {
            ArrayPool<Path>.Shared.Return(buffer);
        }
    }
}
