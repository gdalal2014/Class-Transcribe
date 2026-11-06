using System.Runtime.InteropServices;

namespace ClassScribe.Camera.Windows;

/// <summary>
/// IWSCProductList (iwscapi.h): Windows' Security Center's list of the security products of one kind. A dual interface whose
/// type library Windows does not register, so it is reached through its table of methods: IDispatch's four first, declared
/// only to keep their places and never called, then Initialize, get_Count and get_Item, in the header's order.
/// </summary>
[ComImport, Guid("722A338C-6E8E-4E72-AC27-1417FB0C81C2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWscProductListRaw
{
    /// <summary>IDispatch's GetTypeInfoCount: declared to keep the order; never called.</summary>
    void GetTypeInfoCount();

    /// <summary>IDispatch's GetTypeInfo: declared to keep the order; never called.</summary>
    void GetTypeInfo();

    /// <summary>IDispatch's GetIDsOfNames: declared to keep the order; never called.</summary>
    void GetIDsOfNames();

    /// <summary>IDispatch's Invoke: declared to keep the order; never called.</summary>
    void Invoke();

    /// <summary>Fills the list with the products of <paramref name="provider"/>'s kind, such as WSC_SECURITY_PROVIDER_ANTIVIRUS (4).</summary>
    [PreserveSig] int Initialize(uint provider);

    /// <summary>How many products the list holds.</summary>
    [PreserveSig] int GetCount(out int count);

    /// <summary>The product at <paramref name="index"/>, from 0.</summary>
    [PreserveSig] int GetItem(uint index, [MarshalAs(UnmanagedType.Interface)] out IWscProductRaw product);
}

/// <summary>
/// IWscProduct (iwscapi.h): one security product. IDispatch's four methods first, declared only to keep their places, then
/// get_ProductName and get_ProductState, the two read; the header's later methods are not declared, as nothing calls them.
/// </summary>
[ComImport, Guid("8C38232E-3A45-4A27-92B0-1A16A975F669"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWscProductRaw
{
    /// <summary>IDispatch's GetTypeInfoCount: declared to keep the order; never called.</summary>
    void GetTypeInfoCount();

    /// <summary>IDispatch's GetTypeInfo: declared to keep the order; never called.</summary>
    void GetTypeInfo();

    /// <summary>IDispatch's GetIDsOfNames: declared to keep the order; never called.</summary>
    void GetIDsOfNames();

    /// <summary>IDispatch's Invoke: declared to keep the order; never called.</summary>
    void Invoke();

    /// <summary>The product's name, such as Bitdefender Antivirus.</summary>
    [PreserveSig] int GetProductName([MarshalAs(UnmanagedType.BStr)] out string name);

    /// <summary>The product's state: WSC_SECURITY_PRODUCT_STATE_ON (0), OFF (1), SNOOZED (2) or EXPIRED (3).</summary>
    [PreserveSig] int GetProductState(out int state);
}
