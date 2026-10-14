using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace WinTester.Core.HardwareErrors;

// Turns an event's XML into labelled fields. The header comes from the System element; everything after it
// (EventData, UserData or any other section) is flattened into "name : value" pairs, using the field names
// the provider's manifest gave the data and "Data 1", "Data 2" when it gave none. Pure logic; unparseable
// XML yields an empty result rather than throwing.
public static class WheaEventXmlParser
{
    private const string EmptyValue = "(empty)";

    public static WheaEventData Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return WheaEventData.Empty;

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return new WheaEventData { Xml = xml };
        }

        if (document.Root is not { } root) return new WheaEventData { Xml = xml };

        var system = new List<HardwareErrorField>();
        var data = new List<HardwareErrorField>();

        foreach (var section in root.Elements())
        {
            if (section.Name.LocalName == "System")
                FlattenSystem(section, system);
            else
                Flatten(section, string.Empty, data);
        }

        var record = WheaErrorRecordDecoder.Decode(data);

        return new WheaEventData
        {
            SystemFields = system,
            DataFields = data,
            ErrorRecordFields = record.Fields,
            RecordSeverity = record.Severity,
            Xml = document.ToString()
        };
    }

    // "Provider Name", "EventID", "TimeCreated SystemTime", "Execution ProcessID" and so on: an element's
    // text is listed under its own name and each attribute under "element attribute".
    private static void FlattenSystem(XElement system, List<HardwareErrorField> into)
    {
        foreach (var element in system.Elements())
        {
            var name = element.Name.LocalName;

            var text = element.Value.Trim();
            if (text.Length > 0)
                into.Add(new HardwareErrorField(name, text));

            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                into.Add(new HardwareErrorField($"{name} {attribute.Name.LocalName}", attribute.Value));
        }
    }

    // path is empty for a top-level section, so EventData's fields carry no "EventData/" prefix; nested
    // UserData content is labelled by its element path.
    private static void Flatten(XElement element, string path, List<HardwareErrorField> into)
    {
        if (path.Length > 0)
        {
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                into.Add(new HardwareErrorField($"{path} {attribute.Name.LocalName}", attribute.Value));
        }

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            if (path.Length > 0)
                into.Add(new HardwareErrorField(path, ValueOf(element)));
            return;
        }

        var unnamed = 0;
        foreach (var child in children)
        {
            var childName = child.Name.LocalName;

            if (childName == "Data" && !child.HasElements)
            {
                var label = child.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(label)) label = $"Data {++unnamed}";
                into.Add(new HardwareErrorField(Join(path, label), ValueOf(child)));
            }
            else
            {
                Flatten(child, Join(path, childName), into);
            }
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "/" + name;

    private static string ValueOf(XElement element)
    {
        var text = element.Value.Trim();
        return text.Length == 0 ? EmptyValue : text;
    }
}
