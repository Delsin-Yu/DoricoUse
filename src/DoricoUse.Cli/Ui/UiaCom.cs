using System.Runtime.InteropServices;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// The little bit of COM the dialog reader needs: create the UI Automation object, read a BSTR, release, and call
/// through a vtable.
/// </summary>
/// <remarks>
/// <para>
/// UI Automation is called through raw vtable pointers rather than through <c>[ComImport]</c> interfaces. That is
/// what a Native AOT image can do: the built-in COM marshaller generates its stubs at run time, and this build has
/// no run-time code generation at all. Slot numbers are the 0-based method index after the three IUnknown entries,
/// and they are the only place in this file where a wrong number is possible, so they are named after the method
/// they belong to and kept next to the call that uses them.
/// </para>
/// <para>
/// Every call that hands back an interface pointer hands back an <b>owned</b> reference; whoever asked for it
/// releases it. Nothing here caches, and no element outlives the invocation that read it.
/// </para>
/// </remarks>
internal static unsafe class UiaCom
{
    /// <summary>CLSID_CUIAutomation: the in-process UI Automation server.</summary>
    private static readonly Guid ClsidCuiAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    /// <summary>IID_IUIAutomation.</summary>
    private static readonly Guid IidIuiAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    private const uint ClsctxInprocServer = 1;

    /// <summary>UIA_E_ELEMENTNOTAVAILABLE — the element went away, which for a press means the press worked.</summary>
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    /// <summary>Creates the UI Automation root object. Throws <see cref="CliException"/> when COM says no.</summary>
    public static nint CreateAutomation()
    {
        var clsid = ClsidCuiAutomation;
        var iid = IidIuiAutomation;
        var hr = CoCreateInstance(ref clsid, nint.Zero, ClsctxInprocServer, ref iid, out var instance);

        if (hr < 0 || instance == nint.Zero)
        {
            throw new CliException(
                "DialogProbeFailed",
                $"UI Automation could not be created (CoCreateInstance returned 0x{hr:X8}).",
                DialogModel.ProbeFailureExitCode);
        }

        return instance;
    }

    /// <summary>Drops one owned interface pointer.</summary>
    public static void Release(nint instance)
    {
        if (instance != nint.Zero)
        {
            _ = Marshal.Release(instance);
        }
    }

    /// <summary>A BSTR handed over by UI Automation, copied out and freed. Null and empty both read as "".</summary>
    public static string ReadBstr(nint bstr)
    {
        if (bstr == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringBSTR(bstr) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    /// <summary>A BSTR to hand to COM, owned here and freed by the caller of <see cref="WithBstr"/>.</summary>
    public static nint AllocBstr(string value) => Marshal.StringToBSTR(value);

    public static void FreeBstr(nint bstr)
    {
        if (bstr != nint.Zero)
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with no argument and reads a 32-bit result.</summary>
    public static int CallInt(nint instance, int slot, out int value)
    {
        fixed (int* result = &value)
        {
            var function = (delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, slot);

            return function(instance, result);
        }
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with no argument and reads an interface pointer.</summary>
    public static int CallPointer(nint instance, int slot, out nint value)
    {
        fixed (nint* result = &value)
        {
            var function = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot);

            return function(instance, result);
        }
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with no argument and reads a BSTR.</summary>
    public static int CallBstr(nint instance, int slot, out string value)
    {
        value = string.Empty;
        nint pointer = nint.Zero;

        var function = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot);
        var hr = function(instance, &pointer);

        if (hr < 0)
        {
            return hr;
        }

        value = ReadBstr(pointer);

        return hr;
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with one window handle and reads an interface pointer.</summary>
    public static int CallHandleOutPointer(nint instance, int slot, nint handle, out nint value)
    {
        fixed (nint* result = &value)
        {
            var function = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(instance, slot);

            return function(instance, handle, result);
        }
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with one interface pointer and reads an interface pointer.</summary>
    public static int CallInPointerOutPointer(nint instance, int slot, nint input, out nint value)
    {
        fixed (nint* result = &value)
        {
            var function = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(instance, slot);

            return function(instance, input, result);
        }
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with one integer and reads an interface pointer.</summary>
    public static int CallIntOutPointer(nint instance, int slot, int argument, out nint value)
    {
        fixed (nint* result = &value)
        {
            var function = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(instance, slot);

            return function(instance, argument, result);
        }
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with one integer and reads a BSTR.</summary>
    public static int CallIntOutBstr(nint instance, int slot, int argument, out string value)
    {
        value = string.Empty;
        nint pointer = nint.Zero;

        var function = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(instance, slot);
        var hr = function(instance, argument, &pointer);

        if (hr < 0)
        {
            return hr;
        }

        value = ReadBstr(pointer);

        return hr;
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with one BSTR argument and no result.</summary>
    public static int CallBstrIn(nint instance, int slot, nint bstr)
    {
        var function = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(instance, slot);

        return function(instance, bstr);
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with no argument and no result.</summary>
    public static int CallVoid(nint instance, int slot)
    {
        var function = (delegate* unmanaged[Stdcall]<nint, int>)Slot(instance, slot);

        return function(instance);
    }

    /// <summary>Calls vtable slot <paramref name="slot"/> with an element and reads a rectangle of four ints.</summary>
    public static int CallRect(nint instance, int slot, out (int Left, int Top, int Right, int Bottom) rect)
    {
        var value = stackalloc int[4];
        var function = (delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, slot);
        var hr = function(instance, value);

        rect = hr < 0 ? default : (value[0], value[1], value[2], value[3]);

        return hr;
    }

    private static void* Slot(nint instance, int slot)
    {
        if (instance == nint.Zero)
        {
            throw new CliException(
                "DialogProbeFailed",
                "A UI Automation call was made on an object that had already been released.",
                DialogModel.ProbeFailureExitCode);
        }

        // vtable[0..2] are QueryInterface, AddRef and Release; method N is at vtable[N + 3].
        var vtable = *(void***)instance;

        return vtable[slot + 3];
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        nint outer,
        uint context,
        ref Guid interfaceId,
        out nint instance);
}
