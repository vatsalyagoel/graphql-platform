using System.Text.Json;
using HotChocolate.Fusion.Composition;

namespace HotChocolate.Fusion;

/// <summary>
/// The runtime representation of subgraph-config.json.
/// </summary>
internal sealed record SubgraphConfigJson
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubgraphConfigJson"/> class.
    /// </summary>
    /// <param name="name">
    /// The name of the subgraph.
    /// </param>
    /// <param name="clients">
    /// The list of clients that can be used to fetch data from this subgraph.
    /// </param>
    public SubgraphConfigJson(
        string name,
        IReadOnlyList<IClientConfiguration>? clients = null,
        JsonDocument? extensions = null)
    {
        Name = name;
        Clients = clients ?? Array.Empty<IClientConfiguration>();
    }

    /// <summary>
    /// Gets the name that is used to refer to a subgraph.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Gets the client configurations that are used to fetch data from a subgraph.
    /// </summary>
    public IReadOnlyList<IClientConfiguration> Clients { get; init; }

    /// <summary>
    /// Gets the "extensions" property of the subgraph-config.json.
    /// </summary>
    public JsonDocument? Extensions { get; init; }

    /// <summary>
    /// Deconstructs the <see cref="SubgraphConfigJson"/> into its components.
    /// </summary>
    /// <param name="name">
    /// The name of the subgraph.
    /// </param>
    /// <param name="clients">
    /// The list of clients that can be used to fetch data from this subgraph.
    /// </param>
    public void Deconstruct(
        out string name,
        out IReadOnlyList<IClientConfiguration> clients,
        out JsonDocument? extensions)
    {
        name = Name;
        clients = Clients;
        extensions = Extensions;
    }
}
