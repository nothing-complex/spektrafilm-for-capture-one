using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static void TestNativeVariantLineage(object hosted, object panel, object window, object store)
    {
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var output = Path.Combine(folder, "native-variant-lineage.json");
        var profile = panel.GetType().Assembly.GetType("HostProfile", true)!;
        var selection = profile.GetMethod("ReadSelection", Flags)!;
        var target = profile.GetMethod("CaptureTarget", Flags)!;
        void Select(Guid primary, params Guid[] members) => new FixtureSelection(panel, limit: 40).Select(primary, members);
        JsonNode? Read(Guid id) => Call(store, "Read", id) is { } look ? JsonSerializer.SerializeToNode(look, look.GetType()) : null;
        object Selected() => Prop(selection.Invoke(null, new[] { window })!, "Variant")!;
        Guid Current() => (Guid)Prop(target.Invoke(null, new[] { window })!, "VariantId")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
        var nativeVariant = core.GetType("P1.C1.Core.ICoreVariant", true)!;
        var imageProperty = nativeVariant.GetProperty("CoreImage")!;
        var variantsProperty = imageProperty.PropertyType.GetProperty("CoreVariants")!;
        var uuid = nativeVariant.GetProperty("UUID")!;
        Guid[] RawVariants() => ((IEnumerable)variantsProperty.GetValue(imageProperty.GetValue(Selected()))!)
            .Cast<object>().Select(variant => (Guid)uuid.GetValue(variant)!).ToArray();
        void Execute(string name, bool multiple)
        {
            var command = Field(Field(window, "mCommands")!, name)!;
            var execute = command.GetType().GetMethod("Execute", Flags, null, new[] { typeof(bool) }, null)
                ?? throw new MissingMethodException(command.GetType().FullName, "Execute(Boolean)");
            execute.Invoke(command, new object[] { multiple });
            Call(panel, "SyncSelection");
        }

        var cloneHook = Field(hosted, "cloneHook") as IDisposable;
        Check(cloneHook is not null, "the exact native Clone callback is bound");
        if (phase == "restart")
        {
            var expected = JsonNode.Parse(File.ReadAllText(output))!.AsObject();
            var native = new HashSet<Guid>();
            foreach (var raw in expected["Sources"]!.AsArray())
            {
                var id = raw!.GetValue<Guid>();
                Select(id, id);
                native.UnionWith(RawVariants());
            }
            foreach (var entry in expected["Looks"]!.AsObject())
            {
                var id = Guid.Parse(entry.Key);
                Check(native.Contains(id) && JsonNode.DeepEquals(Read(id), entry.Value),
                    "restart retains the exact native variant and expected independent Spektrafilm look");
            }
            File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native variant lineage assertions");
            return;
        }

        var originals = new FixtureSelection(panel).Variants;
        Check(originals.Length == 4, "the variant lineage test uses the disposable four-RAW fixture");
        var first = originals[0];
        var second = originals[1];
        var change = Field(panel, "exposure")!;
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        void Apply(Guid id, double ev, bool enabled)
        {
            Select(id, id);
            toggle.IsChecked = true;
            change.GetType().GetProperty("Value", Flags)!.SetValue(change, ev);
            toggle.IsChecked = enabled;
        }

        Apply(first, 1.31, enabled: false);
        Apply(second, -.72, enabled: true);
        var firstLook = Read(first)!;
        var secondLook = Read(second)!;
        Select(first, first, second);
        var firstBefore = RawVariants();
        Select(second, second);
        var secondBefore = RawVariants();
        Select(first, first, second);
        Execute("CloneVariant", multiple: false);
        var one = Current();
        Check(RawVariants().Except(firstBefore).SequenceEqual(new[] { one })
            && JsonNode.DeepEquals(Read(one), firstLook) && toggle.IsChecked == false,
            "single native Clone copies the primary RAW look including its disabled master");
        var nativeExposure = nativeVariant.GetProperty("Exposure")!;
        nativeExposure.SetValue(Selected(), .5f);
        Execute("ResetVariant", multiple: false);
        Check(Current() == one && nativeExposure.GetValue(Selected()) is float updated && updated != .5f
            && JsonNode.DeepEquals(Read(one), firstLook), "native Reset resets Capture One exposure on the clone and preserves its saved Spektrafilm look");
        Select(second, second);
        Check(RawVariants().SequenceEqual(secondBefore), "single Clone honors the native Edit Selected policy");

        Select(first, first, second);
        var firstMultiBefore = RawVariants();
        Execute("CloneVariant", multiple: true);
        Select(first, first);
        var firstClone = RawVariants().Except(firstMultiBefore).Single();
        Select(second, second);
        var secondClone = RawVariants().Except(secondBefore).Single();
        Check(JsonNode.DeepEquals(Read(firstClone), firstLook) && JsonNode.DeepEquals(Read(secondClone), secondLook),
            "real multi-variant Clone inherits distinct looks by RAW and preserves each master switch");

        Select(first, first);
        var beforeNew = RawVariants();
        Execute("NewVariant", multiple: false);
        var newVariant = Current();
        Check(RawVariants().Except(beforeNew).SequenceEqual(new[] { newVariant }) && Read(newVariant) is null,
            "native New Variant has fresh Spektrafilm defaults and no inherited look");
        Apply(newVariant, 2.4, enabled: true);
        var newLook = Read(newVariant);
        Select(first, first, newVariant);
        var ambiguousBefore = RawVariants();
        Execute("CloneVariant", multiple: true);
        var ambiguous = RawVariants().Except(ambiguousBefore).ToArray();
        Check(ambiguous.Length == 2 && ambiguous.All(id => Read(id) is null),
            "native Clone creates both same-RAW siblings without assigning uncertain look lineage");
        Check(((TextBlock)Field(panel, "status")!).Text.Contains("Apply copied settings", StringComparison.Ordinal),
            "the panel explains how to copy a look when native lineage is ambiguous");
        Check(JsonNode.DeepEquals(Read(first), firstLook) && JsonNode.DeepEquals(Read(second), secondLook)
            && JsonNode.DeepEquals(Read(newVariant), newLook), "all original variants retain their own saved look after cloning and creating variants");

        var receipt = new JsonObject(new[] { first, second, one, firstClone, secondClone, newVariant }.Concat(ambiguous)
            .Select(id => new KeyValuePair<string, JsonNode?>(id.ToString(), Read(id))));
        var binding = Field(cloneHook!, "binding")!;
        var callbackField = (System.Reflection.FieldInfo)binding.GetType().GetField("Callbacks", Flags)!.GetValue(null)!;
        var cloneCommand = Field(binding, "command")!;
        var wrapper = (Delegate)Field(binding, "wrapper")!;
        var original = (Delegate)Prop(binding, "Original")!;
        var previous = ((Delegate)callbackField.GetValue(cloneCommand)!).GetInvocationList();
        cloneHook!.Dispose();
        var restored = ((Delegate)callbackField.GetValue(cloneCommand)!).GetInvocationList();
        Check(previous.Count(item => item.Equals(wrapper)) == 1
            && restored.SequenceEqual(previous.Select(item => item.Equals(wrapper) ? original : item)),
            "disposing the native Clone hook restores the original callback in place");
        File.WriteAllText(output, new JsonObject { ["Sources"] = JsonSerializer.SerializeToNode(new[] { first, second }),
            ["Looks"] = receipt }.ToJsonString());
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native variant lineage assertions");
    }
}
