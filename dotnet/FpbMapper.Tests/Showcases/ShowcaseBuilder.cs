using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FpbMapper.Tests.Showcases;

/// <summary>
/// Programmatic builder for FPB.js-format JSON. Each <see cref="ProcessNode"/>
/// represents one process (= one decomposition layer) with its SystemLimit,
/// states, POs, TRs and connections. Sub-processes are added with
/// <see cref="ProcessNode.Decompose"/>.
///
/// IDs are derived deterministically from a path so generated JSON is stable
/// across test runs (no random Guids — diffable output).
/// </summary>
internal sealed class ShowcaseBuilder
{
    public string ProjectName { get; }
    public ProcessNode Root { get; }
    private readonly List<ProcessNode> _allProcesses = new();

    public ShowcaseBuilder(string projectName, string rootProcessLabel)
    {
        ProjectName = projectName;
        Root = new ProcessNode(this, parent: null, label: rootProcessLabel, owningPoLabel: null);
        _allProcesses.Add(Root);
    }

    internal void Register(ProcessNode p) => _allProcesses.Add(p);

    public string ToJson()
    {
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["$type"] = "fpb:Project",
                ["name"]  = ProjectName,
                ["targetNamespace"] = "http://www.hsu-ifa.de/fpbjs",
                ["entryPoint"] = Root.Id,
            },
        };
        foreach (var p in _allProcesses) arr.Add(p.BuildWrapper());
        return arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    internal static string IdFor(string path)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(path));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes).ToString();
    }
}

internal sealed class ProcessNode
{
    private readonly ShowcaseBuilder _root;
    private readonly ProcessNode? _parent;
    public string Label { get; }
    public string Id { get; }

    private readonly string _systemLimitId;
    private readonly List<string> _stateIds = new();
    private readonly List<string> _poIds = new();
    private readonly List<string> _subProcessIds = new();
    private readonly Dictionary<string, JsonObject> _elements = new();
    public string? OwningPoLabel { get; }

    public ProcessNode(ShowcaseBuilder root, ProcessNode? parent, string label, string? owningPoLabel)
    {
        _root  = root;
        _parent = parent;
        Label  = label;
        OwningPoLabel = owningPoLabel;
        var path = parent == null ? label : $"{parent.Id}/{label}";
        Id = ShowcaseBuilder.IdFor("proc:" + path);
        _systemLimitId = ShowcaseBuilder.IdFor("sl:" + path);

        _elements[_systemLimitId] = new JsonObject
        {
            ["$type"] = "fpb:SystemLimit",
            ["id"]    = _systemLimitId,
            ["elementsContainer"] = new JsonArray(),
            ["name"]  = label,
        };
    }

    private string AddState(string type, string label, string? sharedId = null)
    {
        var id = sharedId ?? ShowcaseBuilder.IdFor($"state:{Id}/{type}/{label}");
        _elements[id] = new JsonObject
        {
            ["$type"] = type,
            ["id"]    = id,
            ["identification"] = new JsonObject
            {
                ["$type"]         = "fpb:Identification",
                ["uniqueIdent"]   = id,
                ["longName"]      = label,
                ["shortName"]     = label,
                ["versionNumber"] = "1.0",
                ["revisionNumber"]= "0",
            },
            ["isAssignedTo"] = new JsonArray { _systemLimitId },
            ["incoming"]     = new JsonArray(),
            ["outgoing"]     = new JsonArray(),
            ["name"]         = label,
            ["characteristics"] = new JsonArray(),
        };
        _stateIds.Add(id);
        return id;
    }

    public string AddProduct(string label, string? sharedId = null)     => AddState("fpb:Product",     label, sharedId);
    public string AddEnergy(string label, string? sharedId = null)      => AddState("fpb:Energy",      label, sharedId);
    public string AddInformation(string label, string? sharedId = null) => AddState("fpb:Information", label, sharedId);

    public string AddProcessOperator(string label)
    {
        var id = ShowcaseBuilder.IdFor($"po:{Id}/{label}");
        _elements[id] = new JsonObject
        {
            ["$type"] = "fpb:ProcessOperator",
            ["id"]    = id,
            ["identification"] = new JsonObject
            {
                ["$type"]         = "fpb:Identification",
                ["uniqueIdent"]   = id,
                ["longName"]      = label,
                ["shortName"]     = label,
                ["versionNumber"] = "1.0",
                ["revisionNumber"]= "0",
            },
            ["isAssignedTo"]      = new JsonArray { _systemLimitId },
            ["incoming"]          = new JsonArray(),
            ["outgoing"]          = new JsonArray(),
            ["decomposedView"]    = null,
            ["name"]              = label,
            ["characteristics"]   = new JsonArray(),
        };
        _poIds.Add(id);
        return id;
    }

    public string AddTechnicalResource(string label)
    {
        // TR lives *outside* the system limit on the project layer — but still
        // tracked here for simplicity. The mapper places it next to the SL.
        var id = ShowcaseBuilder.IdFor($"tr:{Id}/{label}");
        _elements[id] = new JsonObject
        {
            ["$type"] = "fpb:TechnicalResource",
            ["id"]    = id,
            ["identification"] = new JsonObject
            {
                ["$type"]         = "fpb:Identification",
                ["uniqueIdent"]   = id,
                ["longName"]      = label,
                ["shortName"]     = label,
                ["versionNumber"] = "1.0",
                ["revisionNumber"]= "0",
            },
            ["isAssignedTo"]    = new JsonArray(),
            ["incoming"]        = new JsonArray(),
            ["outgoing"]        = new JsonArray(),
            ["name"]            = label,
            ["characteristics"] = new JsonArray(),
        };
        return id;
    }

    public string AddFlow(string from, string to, string type = "fpb:Flow")
    {
        var id = ShowcaseBuilder.IdFor($"flow:{Id}/{type}/{from}->{to}");
        _elements[id] = new JsonObject
        {
            ["$type"]    = type,
            ["id"]       = id,
            ["sourceRef"]= from,
            ["targetRef"]= to,
        };
        AppendToArray(_elements[from], "outgoing", id);
        AppendToArray(_elements[to],   "incoming", id);
        return id;
    }

    public string AddUsage(string poId, string trId)
    {
        var id = ShowcaseBuilder.IdFor($"usage:{Id}/{poId}->{trId}");
        _elements[id] = new JsonObject
        {
            ["$type"]    = "fpb:Usage",
            ["id"]       = id,
            ["sourceRef"]= poId,
            ["targetRef"]= trId,
        };
        AppendToArray(_elements[trId], "incoming", id);
        return id;
    }

    /// <summary>
    /// Attach a numeric/text characteristic (custom attribute) to an element.
    /// The mapper carries these through to AML as Attribute children.
    /// </summary>
    public void AddCharacteristic(string elementId, string shortName, string longName, string value, string unit)
    {
        if (!_elements.TryGetValue(elementId, out var elem)) return;
        var characteristics = (JsonArray)elem["characteristics"]!;
        var charId = ShowcaseBuilder.IdFor($"char:{elementId}/{shortName}");
        characteristics.Add(new JsonObject
        {
            ["$type"] = "fpbch:Characteristics",
            ["category"] = new JsonObject
            {
                ["$type"]         = "fpb:Identification",
                ["uniqueIdent"]   = charId,
                ["longName"]      = longName,
                ["shortName"]     = shortName,
                ["versionNumber"] = "1.0",
                ["revisionNumber"]= "0",
            },
            ["descriptiveElement"] = new JsonObject
            {
                ["$type"]                     = "fpbch:DescriptiveElement",
                ["valueDeterminationProcess"] = "Festlegung",
                ["representivity"]            = "Sollwert",
                ["setpointValue"] = new JsonObject
                {
                    ["$type"] = "fpbch:ValueWithUnit",
                    ["value"] = value,
                    ["unit"]  = unit,
                },
            },
            ["relationalElement"] = new JsonObject
            {
                ["$type"]               = "fpbch:RelationalElement",
                ["regulationsForRelationalGeneration"] = "",
                ["view"]                = "",
            },
        });
    }

    /// <summary>
    /// Mark a PO as decomposed and return a fresh child <see cref="ProcessNode"/>.
    /// Boundary-state IDs must be re-used between layers so the mapper can
    /// match them.
    /// </summary>
    public ProcessNode Decompose(string poId, string subLabel)
    {
        var owningPoLabel = (string?)_elements[poId]["name"];
        var child = new ProcessNode(_root, this, subLabel, owningPoLabel);
        _root.Register(child);
        _elements[poId]["decomposedView"] = child.Id;
        _subProcessIds.Add(child.Id);
        return child;
    }

    /// <summary>
    /// Add a boundary state that re-uses an upper layer's state ID — the
    /// mapper treats matching IDs as the same conceptual state across layers.
    /// </summary>
    public string AddBoundaryFromParent(string parentStateId, string type, string label)
        => AddState(type, label, sharedId: parentStateId);

    private static void AppendToArray(JsonObject obj, string key, string value)
    {
        if (obj[key] is not JsonArray arr)
        {
            arr = new JsonArray();
            obj[key] = arr;
        }
        arr.Add(value);
    }

    /// <summary>Build the wrapper object that goes into the top-level array.</summary>
    internal JsonObject BuildWrapper()
    {
        // SystemLimit's elementsContainer must list every child id (states +
        // POs + sub-process ids), the process's elementsContainer lists its
        // outermost children (flows-on-project + SL + TRs).
        var sl = _elements[_systemLimitId];
        var slContainer = (JsonArray)sl["elementsContainer"]!;
        foreach (var (id, obj) in _elements)
        {
            if (id == _systemLimitId) continue;
            var t = (string?)obj["$type"];
            // SL contains: states, POs, sub-processes, flows-between-them. TRs
            // and project-level usages stay outside.
            if (t == "fpb:TechnicalResource") continue;
            if (t == "fpb:Usage") continue;
            slContainer.Add(id);
        }
        foreach (var subId in _subProcessIds) slContainer.Add(subId);

        var projectElementsContainer = new JsonArray { _systemLimitId };
        foreach (var (id, obj) in _elements)
        {
            var t = (string?)obj["$type"];
            if (t == "fpb:TechnicalResource" || t == "fpb:Usage")
                projectElementsContainer.Add(id);
        }

        var consistsOfStates = new JsonArray();
        foreach (var sid in _stateIds) consistsOfStates.Add(sid);

        var consistsOfProcessOperator = new JsonArray();
        foreach (var pid in _poIds) consistsOfProcessOperator.Add(pid);

        var process = new JsonObject
        {
            ["$type"]               = "fpb:Process",
            ["id"]                  = Id,
            ["elementsContainer"]   = projectElementsContainer,
            ["isDecomposedProcessOperator"] = _parent == null ? null : new JsonObject
            {
                ["$type"]      = "fpb:ProcessOperator",
                ["id"]         = ShowcaseBuilder.IdFor($"po:{_parent.Id}/{OwningPoLabel}"),
                ["name"]       = OwningPoLabel,
            },
            ["consistsOfStates"]    = consistsOfStates,
            ["consistsOfSystemLimit"]= _systemLimitId,
            ["consistsOfProcesses"] = ToArray(_subProcessIds),
            ["consistsOfProcessOperator"] = consistsOfProcessOperator,
            ["parent"] = _parent == null
                ? new JsonObject
                {
                    ["$type"] = "fpb:Project",
                    ["name"]  = _root.ProjectName,
                    ["targetNamespace"] = "http://www.hsu-ifa.de/fpbjs",
                    ["entryPoint"] = _root.Root.Id,
                }
                : new JsonObject
                {
                    ["$type"] = "fpb:Process",
                    ["id"]    = _parent.Id,
                },
        };

        var elementDataInformation = new JsonArray();
        foreach (var obj in _elements.Values) elementDataInformation.Add(obj.DeepClone());

        return new JsonObject
        {
            ["process"] = process,
            ["elementDataInformation"] = elementDataInformation,
        };
    }

    private static JsonArray ToArray(IEnumerable<string> ids)
    {
        var a = new JsonArray();
        foreach (var id in ids) a.Add(id);
        return a;
    }
}
