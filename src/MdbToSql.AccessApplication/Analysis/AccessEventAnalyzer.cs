using MdbToSql.AccessApplication.Com;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication.Analysis;

internal sealed class AccessEventAnalyzer
{
    private static readonly string[] FormEvents =
    [
        "OnOpen",
        "OnLoad",
        "OnCurrent",
        "BeforeInsert",
        "AfterInsert",
        "BeforeUpdate",
        "AfterUpdate",
        "BeforeDelConfirm",
        "AfterDelConfirm",
        "OnDelete",
        "OnClose",
        "OnActivate",
        "OnDeactivate",
        "OnError",
        "OnTimer"
    ];

    private static readonly string[] CommonControlEvents =
    [
        "OnClick",
        "OnDblClick",
        "OnChange",
        "BeforeUpdate",
        "AfterUpdate",
        "OnEnter",
        "OnExit",
        "OnGotFocus",
        "OnLostFocus",
        "OnKeyDown",
        "OnKeyPress",
        "OnKeyUp",
        "OnMouseDown",
        "OnMouseUp"
    ];

    private static readonly string[] SectionEvents =
    [
        "OnClick",
        "OnDblClick",
        "OnPaint",
        "OnFormat"
    ];

    private readonly ComPropertyReader _reader;

    public AccessEventAnalyzer(ComPropertyReader reader)
    {
        _reader = reader;
    }

    public List<AccessEventBinding> ReadFormEvents(object form, string formName, List<string> warnings)
    {
        return Read(form, formName, AccessObjectKind.Form, FormEvents, warnings);
    }

    public List<AccessEventBinding> ReadControlEvents(
        object control,
        string controlName,
        List<string> warnings)
    {
        return Read(control, controlName, AccessObjectKind.Control, CommonControlEvents, warnings);
    }

    public List<AccessEventBinding> ReadSectionEvents(
        object section,
        string sectionName,
        List<string> warnings)
    {
        return Read(section, sectionName, AccessObjectKind.Section, SectionEvents, warnings);
    }

    private List<AccessEventBinding> Read(
        object target,
        string objectName,
        AccessObjectKind objectKind,
        IReadOnlyList<string> eventNames,
        List<string> warnings)
    {
        var bindings = new List<AccessEventBinding>();
        foreach (var eventName in eventNames)
        {
            var expression = _reader.ReadString(target, eventName, warnings);
            var binding = AccessEventBindingClassifier.Bind(objectName, objectKind, eventName, expression);
            if (binding is not null)
            {
                bindings.Add(binding);
            }
        }

        return bindings;
    }
}
