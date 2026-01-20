using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using ServiceScan.SourceGenerator.Model;
using static ServiceScan.SourceGenerator.DiagnosticDescriptors;

namespace ServiceScan.SourceGenerator;

public partial class DependencyInjectionGenerator
{
    private static readonly string[] ExcludedInterfaces = [
        "System.IDisposable",
        "System.IAsyncDisposable"
    ];

    private static DiagnosticModel<MethodImplementationModel> FindServicesToRegister((DiagnosticModel<MethodWithAttributesModel>, Compilation) context)
    {
        var (diagnosticModel, compilation) = context;
        var diagnostic = diagnosticModel.Diagnostic;

        if (diagnostic != null)
            return diagnostic;

        var (method, attributes) = diagnosticModel.Model;

        var containingType = compilation.GetTypeByMetadataName(method.TypeMetadataName);
        var registrations = new List<ServiceRegistrationModel>();
        var customHandlers = new List<CustomHandlerModel>();

        foreach (var attribute in attributes)
        {
            bool typesFound = false;

            foreach (var (implementationType, matchedTypes, attributeData) in FilterTypes(compilation, attribute, containingType))
            {
                typesFound = true;

                if (attribute.CustomHandler != null)
                {
                    var implementationTypeName = implementationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    
                    // Generate attribute instantiation code if needed
                    string? attributeInstantiation = null;
                    if (attribute.CustomHandlerParameterMode == CustomHandlerParameterMode.WithAttribute && attributeData != null)
                    {
                        attributeInstantiation = GenerateAttributeInstantiation(attributeData);
                    }

                    // If CustomHandler method has multiple type parameters, which are resolvable from the first one - we try to provide them.
                    // e.g. ApplyConfiguration<T, TEntity>(ModelBuilder modelBuilder) where T : IEntityTypeConfiguration<TEntity>
                    if (attribute.CustomHandlerMethodTypeParametersCount > 1 && matchedTypes != null)
                    {
                        foreach (var matchedType in matchedTypes)
                        {
                            EquatableArray<string> typeArguments =
                                [
                                    implementationTypeName,
                                    .. matchedType.TypeArguments.Select(a => a.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                                ];

                            customHandlers.Add(new CustomHandlerModel(
                                attribute.CustomHandlerType.Value,
                                attribute.CustomHandler,
                                implementationTypeName,
                                typeArguments,
                                attributeInstantiation));
                        }
                    }
                    else
                    {
                        customHandlers.Add(new CustomHandlerModel(
                            attribute.CustomHandlerType.Value,
                            attribute.CustomHandler,
                            implementationTypeName,
                            [implementationTypeName],
                            attributeInstantiation));
                    }
                }
                else
                {
                    var serviceTypes = (attribute.AsSelf, attribute.AsImplementedInterfaces) switch
                    {
                        (true, true) => [implementationType, .. GetSuitableInterfaces(implementationType)],
                        (false, true) => GetSuitableInterfaces(implementationType),
                        (true, false) => [implementationType],
                        _ => matchedTypes ?? [implementationType]
                    };

                    foreach (var serviceType in serviceTypes)
                    {
                        if (implementationType.IsGenericType)
                        {
                            var implementationTypeName = implementationType.ConstructUnboundGenericType().ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            var serviceTypeName = serviceType.IsGenericType
                                ? serviceType.ConstructUnboundGenericType().ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                : serviceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                            var registration = new ServiceRegistrationModel(
                                attribute.Lifetime,
                                serviceTypeName,
                                implementationTypeName,
                                ResolveImplementation: false,
                                IsOpenGeneric: true,
                                attribute.KeySelector,
                                attribute.KeySelectorType);

                            registrations.Add(registration);
                        }
                        else
                        {
                            var shouldResolve = attribute.AsSelf && attribute.AsImplementedInterfaces && !SymbolEqualityComparer.Default.Equals(implementationType, serviceType);
                            var registration = new ServiceRegistrationModel(
                                attribute.Lifetime,
                                serviceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                implementationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                shouldResolve,
                                IsOpenGeneric: false,
                                attribute.KeySelector,
                                attribute.KeySelectorType);

                            registrations.Add(registration);
                        }
                    }
                }
            }

            if (!typesFound)
                diagnostic ??= Diagnostic.Create(NoMatchingTypesFound, attribute.Location);
        }

        var implementationModel = new MethodImplementationModel(method, [.. registrations], [.. customHandlers]);
        return new(diagnostic, implementationModel);
    }

    private static IEnumerable<INamedTypeSymbol> GetSuitableInterfaces(ITypeSymbol type)
    {
        return type.AllInterfaces.Where(x => !ExcludedInterfaces.Contains(x.ToDisplayString()));
    }

    private static string GenerateAttributeInstantiation(AttributeData attributeData)
    {
        var attributeClass = attributeData.AttributeClass;
        var attributeTypeName = attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        
        // Build constructor arguments
        var constructorArgs = string.Join(", ", attributeData.ConstructorArguments.Select(FormatTypedConstant));
        
        // Build named arguments
        var namedArgs = attributeData.NamedArguments.Length > 0
            ? " { " + string.Join(", ", attributeData.NamedArguments.Select(kvp => $"{kvp.Key} = {FormatTypedConstant(kvp.Value)}")) + " }"
            : "";
        
        return $"new {attributeTypeName}({constructorArgs}){namedArgs}";
    }
    
    private static string FormatTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull)
            return "null";
            
        return constant.Kind switch
        {
            TypedConstantKind.Primitive => constant.Type?.SpecialType switch
            {
                SpecialType.System_String => $"\"{constant.Value}\"",
                SpecialType.System_Char => $"'{constant.Value}'",
                SpecialType.System_Boolean => constant.Value?.ToString()?.ToLowerInvariant() ?? "false",
                SpecialType.System_Single => $"{constant.Value}f",
                SpecialType.System_Double => $"{constant.Value}d",
                SpecialType.System_Decimal => $"{constant.Value}m",
                _ => constant.Value?.ToString() ?? "null"
            },
            TypedConstantKind.Enum => $"({constant.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){constant.Value}",
            TypedConstantKind.Type => $"typeof({((ITypeSymbol)constant.Value!).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})",
            TypedConstantKind.Array => $"new[] {{ {string.Join(", ", constant.Values.Select(FormatTypedConstant))} }}",
            _ => constant.Value?.ToString() ?? "null"
        };
    }
}
