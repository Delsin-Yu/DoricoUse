namespace DoricoUse.Cli.Ui;

/// <summary>
/// The UI Automation control type ids this build names, and the one rule that decides which of them an agent may
/// act on.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and that is the point: the table is what every element read filters on, so a wrong number here is a
/// capability that silently disappears. It has already happened once — <c>ListItem</c> was written as <c>50029</c>,
/// which is <c>DataItem</c>, so no list row of Dorico's Setup lists or of the 繁衍声部格式 dialog ever reached a
/// payload while the tool walked straight past it. Keeping the table in a file the check projects compile is what
/// makes that checkable without a live Dorico.
/// </para>
/// <para>
/// The numbers are the client interface definitions' control type ids, not something measured off Dorico.
/// <b>Measured</b> is the mapping itself, on this installation: Dorico's Setup player, flow and layout lists
/// expose their rows as <c>50007</c> (read with an independent walker, and again through this build's own
/// <c>PanelReader</c> after the number was corrected), a Qt check box as <c>50002</c>, the QML edit field of a
/// renamed player as <c>50004</c>, and the classic file picker's file-name box as <c>50004</c> inside a
/// <c>50003</c>.
/// </para>
/// </remarks>
internal static class ControlTypes
{
    public const int Button = 50000;

    public const int CheckBox = 50002;

    public const int ComboBox = 50003;

    public const int Edit = 50004;

    /// <summary>A row of a list. <b>50007</b> — this is the one the build got wrong.</summary>
    public const int ListItem = 50007;

    public const int List = 50008;

    public const int MenuBar = 50010;

    public const int MenuItem = 50011;

    public const int RadioButton = 50013;

    public const int ScrollBar = 50014;

    public const int Tab = 50018;

    public const int TabItem = 50019;

    public const int Text = 50020;

    public const int ToolBar = 50021;

    public const int Tree = 50023;

    public const int TreeItem = 50024;

    public const int Custom = 50025;

    public const int Group = 50026;

    /// <summary>A grid that holds rows. A row container, like a list or a tree.</summary>
    public const int DataGrid = 50028;

    /// <summary>A row of a data grid. Not a list row: the two are different control types with different ids.</summary>
    public const int DataItem = 50029;

    public const int SplitButton = 50031;

    public const int Window = 50032;

    public const int Pane = 50033;

    public const int Header = 50034;

    public const int HeaderItem = 50035;

    public const int TitleBar = 50037;

    public const int Separator = 50038;

    /// <summary>A table that holds rows. The other half of <see cref="DataGrid"/>.</summary>
    public const int Table = 50036;

    /// <summary>
    /// Whether an element is a row of a list, a tree or a data grid. Measured: Dorico's Setup rows and the
    /// 繁衍声部格式 dialog's rows are <c>ListItem</c> (50007) — the number this build once had wrong — and a file
    /// picker's file list contributes <c>DataItem</c> rows.
    /// </summary>
    public static bool IsRow(int controlType) => controlType is ListItem or TreeItem or DataItem;

    /// <summary>
    /// Whether an element is a container that holds rows. <b>Measured</b>: <see cref="List"/> is what Dorico's
    /// Setup lists and the 繁衍声部格式 dialog's lists are (their rows are <c>ListItem</c> children), and a
    /// <see cref="Tree"/> is what a file picker's folder pane is. The grid and table types are the standard ones
    /// and are here so that a row of either still gets a container name rather than none.
    /// </summary>
    public static bool IsRowContainer(int controlType) => controlType is List or Tree or DataGrid or Table;

    /// <summary>
    /// Whether an element is one an action could target. A text label, a group, a pane, a list or a tree is not:
    /// they are what a window is made of rather than what an agent operates, and a read that kept them would bury
    /// the few elements that matter (measured: the library manager's 27 nodes are four levels of panes, groups and
    /// labels around five operable elements).
    /// </summary>
    /// <remarks>
    /// What is in the list is a measurement, not a guess: each of these control types was read off a real Dorico
    /// window and each of them carries at least one pattern an action can use. <see cref="ListItem"/> is here
    /// because Dorico's Setup lists expose their rows that way.
    /// </remarks>
    public static bool IsActionable(int controlType) => controlType is
        Button or
        CheckBox or
        ComboBox or
        Edit or
        ListItem or
        MenuItem or
        RadioButton or
        TabItem or
        TreeItem or
        DataItem or
        SplitButton;

    /// <summary>The name of a control type, for a payload an agent reads. The number for anything unnamed.</summary>
    public static string Name(int controlType) => controlType switch
    {
        Button => "Button",
        CheckBox => "CheckBox",
        ComboBox => "ComboBox",
        Edit => "Edit",
        ListItem => "ListItem",
        List => "List",
        MenuBar => "MenuBar",
        MenuItem => "MenuItem",
        RadioButton => "RadioButton",
        ScrollBar => "ScrollBar",
        Tab => "Tab",
        TabItem => "TabItem",
        Text => "Text",
        ToolBar => "ToolBar",
        Tree => "Tree",
        TreeItem => "TreeItem",
        Custom => "Custom",
        Group => "Group",
        DataGrid => "DataGrid",
        DataItem => "DataItem",
        SplitButton => "SplitButton",
        Window => "Window",
        Pane => "Pane",
        Header => "Header",
        HeaderItem => "HeaderItem",
        TitleBar => "TitleBar",
        Separator => "Separator",
        Table => "Table",
        _ => $"controlType({controlType})",
    };

    /// <summary>The role an agent sees for one control type.</summary>
    public static string Role(int controlType) => controlType switch
    {
        Button => "button",
        CheckBox => "checkBox",
        ComboBox => "comboBox",
        Edit => "edit",
        ListItem => "listItem",
        List => "list",
        MenuItem => "menuItem",
        RadioButton => "radioButton",
        Tab => "tab",
        TabItem => "tabItem",
        Tree => "tree",
        TreeItem => "treeItem",
        Group => "group",
        DataGrid => "dataGrid",
        DataItem => "dataItem",
        SplitButton => "splitButton",
        Pane => "pane",
        TitleBar => "titleBar",
        Separator => "separator",
        Table => "table",
        Text => "text",
        _ => "controlType-" + controlType.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
