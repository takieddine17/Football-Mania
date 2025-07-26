using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Unity.Services.Relay.Editor.MigrationPath
{
    static class Validator
    {
        const string k_ThisPackageIdentifier = "com.unity.services.relay";

        const string k_MultiplayerSDKPackageIdentifier =
            "com.unity.services.multiplayer";

        const string k_FullyQualifiedLobbyHandler =
            "Unity.Services.Lobby.Editor.MigrationPath.Validator.OnRegisteredPackages";

        const string k_FullyQualifiedMatchmakerHandler =
            "Unity.Services.Matchmaker.Editor.MigrationPath.Validator.OnRegisteredPackages";

        const string k_FullyQualifiedMultiplayHandler =
            "Unity.Services.Multiplay.Editor.MigrationPath.Validator.OnRegisteredPackages";

        const string k_FullyQualifiedMultiplayerHandler =
            "Unity.Services.Multiplayer.Editor.MigrationPath.Validator.OnRegisteredPackages";

        const string k_MigrationDocumentationURL =
            "https://docs.unity.com/ugs/en-us/manual/mps-sdk/manual/migration-path";

        const string k_UnityMultiplayerServices = "Unity Multiplayer Services";

        static readonly string k_ThisPackageFullyQualifiedHandler =
            $"{typeof(Validator).FullName}.{nameof(OnRegisteredPackages)}";

        //@formatter:off
        static readonly string[] k_FullyQualifiedHandlers =
        {
            k_FullyQualifiedLobbyHandler,
            k_FullyQualifiedMatchmakerHandler,
            k_FullyQualifiedMultiplayHandler,
            k_FullyQualifiedMultiplayerHandler,
            k_ThisPackageFullyQualifiedHandler
        };
        //@formatter:on

        [InitializeOnLoadMethod]
        private static void SubscribeToPackageManagerEvents()
        {
            Events.registeredPackages -= OnRegisteredPackages;
            Events.registeredPackages += OnRegisteredPackages;
        }

        private static string WarningMessage(
            in ReadOnlyCollection<PackageInfo> items)
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine(
                $"The following {(items.Count == 1 ? "package has" : "packages have")} been added:");
            foreach (var packageInfo in items)
            {
                stringBuilder.AppendLine(
                    $"\t- {packageInfo.displayName} ({packageInfo.name}) version {packageInfo.version}");
            }

            stringBuilder.AppendLine(
                $"However, {(items.Count == 1 ? "it is" : "they are")} incompatible with the " +
                "Unity Multiplayer Service SDK.");
            stringBuilder.AppendLine(
                $"Please remove the following {(items.Count == 1 ? "package" : "packages")}:");
            foreach (var packageInfo in items)
            {
                stringBuilder.AppendLine(
                    $"\t- {packageInfo.displayName} ({packageInfo.name}) version {packageInfo.version}");
            }

            stringBuilder.AppendLine(
                "If you wish to use the Unity Multiplayer Services SDK.");
            return stringBuilder.ToString();
        }

        private static string WarningMessageWithDependency(
            in ReadOnlyCollection<IGrouping<PackageInfo, DependencyInfo>>
                packages)
        {
            var stringBuilder = new StringBuilder();
            foreach (var group in packages)
            {
                stringBuilder.AppendLine(
                    $"The following {(packages.Count == 1 ? "package has an" : "packages have")} incompatible {(group.Count() > 1 ? "dependencies" : "dependency")} with the Multipalyer Services SDK:");
                stringBuilder.AppendLine(
                    $"\t- {group.Key.name} version {group.Key.version}");

                stringBuilder.AppendLine(
                    $"The incompatible {(group.Count() > 1 ? "dependencies are" : "dependency is")}:");
                foreach (var dependency in group)
                {
                    stringBuilder.AppendLine(
                        $"\t- {dependency.name} version {dependency.version}");
                }
            }

            stringBuilder.AppendLine(
                $"Please remove the following {(packages.Count == 1 ? "package" : "packages")}:");
            foreach (var group in packages)
            {
                stringBuilder.AppendLine(
                    $"\t- {group.Key.displayName} ({group.Key.name}) version {group.Key.version}");
            }

            stringBuilder.AppendLine(
                "If you wish to use the Unity Multiplayer Services SDK.");
            return stringBuilder.ToString();
        }

        private static void OnRegisteredPackages(
            PackageRegistrationEventArgs eventArgs)
        {
            try
            {
                var isThisPackageRemoved = eventArgs.removed
                    .Select(packageInfo => packageInfo.name)
                    .Contains(k_ThisPackageIdentifier);
                if (isThisPackageRemoved)
                {
                    Events.registeredPackages -= OnRegisteredPackages;
                    return;
                }

                if (!IsFirst(k_FullyQualifiedHandlers))
                {
                    return;
                }

                var compatibilityInfo = CheckCompatibility(eventArgs.added);

                if (compatibilityInfo.IsCompatible)
                {
                    return;
                }

                var message = compatibilityInfo.PackageWithDependencies.Any()
                    ? WarningMessageWithDependency(compatibilityInfo
                        .PackageWithDependencies)
                    : WarningMessage(compatibilityInfo
                        .PackageWithoutDependencies);
                Debug.LogWarning(message);

                var choice = EditorUtility.DisplayDialogComplex(
                    k_UnityMultiplayerServices,
                    message, "Ok", "Close", "Help");
                if (choice == 2)
                {
                    Application.OpenURL(k_MigrationDocumentationURL);
                }
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"Error processing package registration event: {e.Message}");
            }

            return;

            static bool IsFirst(string[] listOfMethodInfo)
            {
                var eventField = typeof(Events).GetField(
                    nameof(Events.registeredPackages),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (eventField == null)
                {
                    throw new MissingMethodException(nameof(Events),
                        nameof(Events.registeredPackages));
                }

                var invocationList =
                    ((MulticastDelegate)eventField.GetValue(null))
                    .GetInvocationList();
                return invocationList
                           .Select(d =>
                               $"{d.Method.DeclaringType.FullName}.{d.Method.Name}")
                           .FirstOrDefault(listOfMethodInfo.Contains) ==
                       k_ThisPackageFullyQualifiedHandler;
            }
        }

        internal static CompatibilityInfo CheckCompatibility(
            in ReadOnlyCollection<PackageInfo> added)
        {
            var incompatiblePackages = added
                .Where(packageInfo =>
                    k_MultiplayerSDKPackageIdentifier == packageInfo.name)
                .ToList();

            var incompatibleDependentPackages = added.SelectMany(
                    packageInfo =>
                        packageInfo.dependencies
                            .Where(dependency =>
                                k_MultiplayerSDKPackageIdentifier ==
                                dependency.name)
                            .Select(dependency => new
                            {
                                Package = packageInfo,
                                Dependency = dependency
                            }))
                .GroupBy(tuple => tuple.Package, tuple => tuple.Dependency)
                .Where(group => group.Any())
                .ToList();

            return new CompatibilityInfo(
                incompatibleDependentPackages.AsReadOnly(),
                incompatiblePackages.AsReadOnly());
        }

        internal readonly struct CompatibilityInfo
        {
            public readonly
                ReadOnlyCollection<IGrouping<PackageInfo, DependencyInfo>>
                PackageWithDependencies;

            public readonly ReadOnlyCollection<PackageInfo>
                PackageWithoutDependencies;

            public bool IsCompatible =>
                !PackageWithDependencies.Any() &&
                !PackageWithoutDependencies.Any();

            public CompatibilityInfo(
                ReadOnlyCollection<IGrouping<PackageInfo, DependencyInfo>>
                    packageWithDependencies,
                ReadOnlyCollection<PackageInfo> packageWithoutDependencies)
            {
                PackageWithDependencies = packageWithDependencies;
                PackageWithoutDependencies = packageWithoutDependencies;
            }
        }
    }
}
