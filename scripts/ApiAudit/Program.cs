// Source audit only. The authored OpenAPI document remains the API contract.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

var source = Path.GetFullPath(args[0]);
var references = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement
    .GetProperty("Items").GetProperty("ReferencePath").EnumerateArray()
    .Select(x => MetadataReference.CreateFromFile(x.GetProperty("Identity").GetString()!));
var trees = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
    .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p),
        new CSharpParseOptions(preprocessorSymbols: new[] { "RIMWORLD_1_6" }), p)).ToArray();
var compilation = CSharpCompilation.Create("ApiAudit", trees, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default));
var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
if (errors.Length != 0) {
    foreach (var error in errors.Take(20)) Console.Error.WriteLine(error);
    Environment.Exit(1);
}
var types = new Dictionary<string, object>();
string TypeName(ITypeSymbol type) => type.ToDisplayString();
object Describe(ITypeSymbol type) {
    var name = TypeName(type);
    if (types.ContainsKey(name)) return new { type = name };
    types[name] = new { pending = true };
    if (type is IArrayTypeSymbol array) {
        Describe(array.ElementType);
        types[name] = new { kind = "array", element = TypeName(array.ElementType) };
    } else if (type is INamedTypeSymbol named) {
        foreach (var arg in named.TypeArguments) Describe(arg);
        var members = new List<object>();
        if (named.ContainingAssembly?.Name == "ApiAudit" || named.IsAnonymousType) {
            for (var current = named; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                foreach (var member in current.GetMembers().Where(m => !m.IsStatic && m.DeclaredAccessibility == Accessibility.Public)) {
                    var mt = member is IPropertySymbol prop && prop.GetMethod != null ? prop.Type : member is IFieldSymbol field ? field.Type : null;
                    if (mt == null) continue;
                    Describe(mt);
                    members.Add(new { name = member.Name, type = TypeName(mt), attributes = member.GetAttributes().Select(a => new { name = a.AttributeClass?.Name, args = a.ConstructorArguments.Select(x => x.Value), named = a.NamedArguments.ToDictionary(x => x.Key, x => x.Value.Value) }), comment = member.GetDocumentationCommentXml() });
                }
            }
        }
        types[name] = new { kind = type.TypeKind.ToString(), special = type.SpecialType.ToString(), external = named.ContainingAssembly?.Name != "ApiAudit" && !named.IsAnonymousType,
            generic = named.ConstructedFrom.ToDisplayString(), arguments = named.TypeArguments.Select(TypeName), members,
            values = named.TypeKind == TypeKind.Enum ? named.GetMembers().OfType<IFieldSymbol>().Where(x => x.HasConstantValue).ToDictionary(x => x.Name, x => x.ConstantValue) : null };
    } else types[name] = new { kind = type.TypeKind.ToString() };
    return new { type = name };
}
var routes = new List<object>();
foreach (var tree in trees) {
    var model = compilation.GetSemanticModel(tree);
    foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()) {
        var symbol = model.GetDeclaredSymbol(method)!;
        foreach (var attribute in symbol.GetAttributes()) {
            var attrName = attribute.AttributeClass?.Name;
            if (!new[] { "GetAttribute", "PostAttribute", "PutAttribute", "DeleteAttribute", "PatchAttribute", "RouteAttribute" }.Contains(attrName)) continue;
            var arguments = attribute.ConstructorArguments.Select(a => a.Value?.ToString()).ToArray();
            var calls = new List<object>();
            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                var called = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                if (called == null) continue;
                var name = called.Name;
                if (!(name.StartsWith("Send") || name.StartsWith("Get") && called.ContainingType.Name == "RequestParser" || name == "ReadBodyAsync" || name == "Handle" || name == "CacheAwareResponseAsync")) continue;
                foreach (var arg in called.TypeArguments) Describe(arg);
                calls.Add(new { name, owner = called.ContainingType.ToDisplayString(), generics = called.TypeArguments.Select(TypeName),
                    args = call.ArgumentList.Arguments.Select(a => new { text = a.ToString(), constant = model.GetConstantValue(a.Expression).HasValue ? model.GetConstantValue(a.Expression).Value : null, type = model.GetTypeInfo(a.Expression).Type is {} t ? TypeName(t) : null }).ToArray() });
                foreach (var arg in call.ArgumentList.Arguments) if (model.GetTypeInfo(arg.Expression).Type is {} t) Describe(t);
            }
            routes.Add(new { method = attrName == "RouteAttribute" ? arguments[0] : attrName!.Replace("Attribute", "").ToUpperInvariant(), path = arguments.Last(),
                handler = symbol.Name, controller = symbol.ContainingType.Name, file = Path.GetRelativePath(source, tree.FilePath).Replace('\\', '/'),
                line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                registered = symbol.Parameters.Length == 1 && symbol.Parameters[0].Type.Name == "HttpListenerContext",
                description = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "EndpointMetadataAttribute")?.ConstructorArguments.FirstOrDefault().Value,
                calls, body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() });
        }
    }
}
File.WriteAllText(args[2], JsonSerializer.Serialize(new { routes, types }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Audited {routes.Count} attributed routes and {types.Count} referenced types.");
