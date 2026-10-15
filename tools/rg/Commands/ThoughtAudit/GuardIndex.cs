using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>A declared member of an indexed type. Kind is field, prop, method, enum or type.</summary>
internal sealed record Member(string Kind, string Owner, string Name, string? Type, bool Virtual, SyntaxNode Node);

/// <summary>
/// The syntax trees of the decompiled game sources plus a by-name index of their types and members (#3218). Syntax only: no
/// compilation and no references, so anything the sources do not declare (Unity, the BCL) is an unresolved symbol by
/// construction. Pure; the guard analysis and its unit test share it.
/// </summary>
internal sealed class SourceIndex
{
    private readonly Dictionary<string, List<BaseTypeDeclarationSyntax>> types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> typeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MethodDeclarationSyntax>> extensions = new(StringComparer.Ordinal);

    private SourceIndex(IReadOnlyList<SyntaxTree> trees)
    {
        Trees = trees;
    }

    /// <summary>The parsed files in the order given.</summary>
    public IReadOnlyList<SyntaxTree> Trees { get; }

    /// <summary>Parses the files (in parallel, results kept in input order) and indexes their types.</summary>
    public static SourceIndex Build(IReadOnlyList<(string Path, string Text)> files)
    {
        var trees = new SyntaxTree[files.Count];
        Parallel.For(0, files.Count, i => trees[i] = CSharpSyntaxTree.ParseText(files[i].Text, path: files[i].Path));
        var index = new SourceIndex(trees);
        foreach (var tree in trees)
        {
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                index.Add(decl);
            }
        }
        return index;
    }

    public bool HasType(string name) => types.ContainsKey(name);

    /// <summary>True when two differently-qualified types share the simple name, so a bare name cannot be resolved.</summary>
    public bool Ambiguous(string name) => typeKeys.TryGetValue(name, out var keys) && keys.Count > 1;

    public IReadOnlyList<BaseTypeDeclarationSyntax> Declarations(string name) =>
        types.TryGetValue(name, out var list) ? list : [];

    /// <summary>The type, then its base types and interfaces as written, breadth first; names the sources do not declare stay in.</summary>
    public IReadOnlyList<string> Chain(string type)
    {
        var order = new List<string> { type };
        for (var i = 0; i < order.Count; i++)
        {
            foreach (var decl in Declarations(order[i]))
            {
                foreach (var b in decl.BaseList?.Types ?? [])
                {
                    var name = TypeName(b.Type);
                    if (name is not null && !order.Contains(name))
                    {
                        order.Add(name);
                    }
                }
            }
        }
        return order;
    }

    public bool IsSubtype(string type, string ancestor) => Chain(type).Contains(ancestor);

    /// <summary>
    /// The members named <paramref name="name"/> in the nearest type of the chain that declares any (overloads share a type).
    /// <paramref name="external"/> reports that the chain reaches a type the sources do not declare, so absence is not proof.
    /// </summary>
    public IReadOnlyList<Member> Find(string type, string name, out bool external)
    {
        external = false;
        foreach (var t in Chain(type))
        {
            if (!HasType(t))
            {
                external = true;
                continue;
            }
            var found = MembersOf(t).Where(m => m.Name == name).ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }
        return [];
    }

    /// <summary>Extension methods named <paramref name="name"/> whose first parameter type is in the receiver's chain.</summary>
    public IReadOnlyList<Member> FindExtension(string receiver, string name)
    {
        if (!extensions.TryGetValue(name, out var list))
        {
            return [];
        }
        var chain = Chain(receiver);
        var result = new List<Member>();
        foreach (var m in list)
        {
            var first = TypeName(m.ParameterList.Parameters[0].Type);
            if (first is not null && chain.Contains(first))
            {
                result.Add(new Member("method", OwnerOf(m), name, TypeName(m.ReturnType), false, m));
            }
        }
        return result;
    }

    public IReadOnlyList<Member> MembersOf(string type)
    {
        var result = new List<Member>();
        foreach (var decl in Declarations(type))
        {
            var isInterface = decl is InterfaceDeclarationSyntax;
            switch (decl)
            {
                case EnumDeclarationSyntax e:
                    result.AddRange(e.Members.Select(m => new Member("enum", type, m.Identifier.Text, type, false, m)));
                    break;
                case TypeDeclarationSyntax td:
                    foreach (var m in td.Members)
                    {
                        AddMember(result, type, m, isInterface);
                    }
                    break;
                default:
                    break;
            }
        }
        return result;
    }

    /// <summary>The simple name a type syntax denotes: generics, qualification, nullable and ref stripped; null for arrays and tuples.</summary>
    public static string? TypeName(TypeSyntax? t) => t switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => TypeName(q.Right),
        AliasQualifiedNameSyntax a => TypeName(a.Name),
        NullableTypeSyntax n => TypeName(n.ElementType),
        RefTypeSyntax r => TypeName(r.Type),
        PredefinedTypeSyntax p => p.Keyword.Text,
        _ => null,
    };

    /// <summary>The name of the type declaring a node, with enclosing types joined by '.'; empty at file level.</summary>
    public static string OwnerOf(SyntaxNode node) =>
        node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";

    private static void AddMember(List<Member> result, string owner, MemberDeclarationSyntax m, bool isInterface)
    {
        switch (m)
        {
            case FieldDeclarationSyntax f:
                result.AddRange(f.Declaration.Variables.Select(v => new Member("field", owner, v.Identifier.Text, TypeName(f.Declaration.Type), false, v)));
                break;
            case PropertyDeclarationSyntax p:
                result.Add(new Member("prop", owner, p.Identifier.Text, TypeName(p.Type), isInterface || IsVirtual(p.Modifiers), p));
                break;
            case MethodDeclarationSyntax md:
                result.Add(new Member("method", owner, md.Identifier.Text, TypeName(md.ReturnType), isInterface || IsVirtual(md.Modifiers), md));
                break;
            case OperatorDeclarationSyntax op:
                result.Add(new Member("operator", owner, op.OperatorToken.Text, TypeName(op.ReturnType), false, op));
                break;
            case BaseTypeDeclarationSyntax nested:
                result.Add(new Member("type", owner, nested.Identifier.Text, nested.Identifier.Text, false, nested));
                break;
            default:
                break;
        }
    }

    private static bool IsVirtual(SyntaxTokenList modifiers) =>
        modifiers.Any(t => t.IsKind(SyntaxKind.VirtualKeyword) || t.IsKind(SyntaxKind.AbstractKeyword) || t.IsKind(SyntaxKind.OverrideKeyword));

    private void Add(BaseTypeDeclarationSyntax decl)
    {
        var name = decl.Identifier.Text;
        if (!types.TryGetValue(name, out var list))
        {
            list = [];
            types[name] = list;
            typeKeys[name] = new HashSet<string>(StringComparer.Ordinal);
        }
        list.Add(decl);
        var ns = string.Join('.', decl.Ancestors().Where(a => a is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax)
            .Select(a => a is BaseNamespaceDeclarationSyntax n ? n.Name.ToString() : ((BaseTypeDeclarationSyntax)a).Identifier.Text).Reverse());
        typeKeys[name].Add(ns);
        if (decl is ClassDeclarationSyntax { Modifiers: var mods } c && mods.Any(t => t.IsKind(SyntaxKind.StaticKeyword)))
        {
            foreach (var m in c.Members.OfType<MethodDeclarationSyntax>().Where(m => m.ParameterList.Parameters.Count > 0
                && m.ParameterList.Parameters[0].Modifiers.Any(t => t.IsKind(SyntaxKind.ThisKeyword))))
            {
                if (!extensions.TryGetValue(m.Identifier.Text, out var ext))
                {
                    ext = [];
                    extensions[m.Identifier.Text] = ext;
                }
                ext.Add(m);
            }
        }
    }
}
