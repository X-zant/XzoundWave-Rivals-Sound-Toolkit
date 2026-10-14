using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace MRAudioKit;

/// <summary>
/// UAssetAPI records whether a property was ZERO when it read an unversioned asset
/// (<c>PropertyData.IsZero</c>, MainSerializer.cs:347) and, on writing, trusts that record
/// both for the header's zero mask (:163) and for whether the value is written at all (:591).
/// It never looks at the value again. So a field read as zero that is then given a value --
/// HeroID 0 to 1034 on a copied KillVoices entry, bEnemyHear false to true on a copied row --
/// is written as zero, its value dropped, and every property after it misread.
///
/// A second rule decides which header is written: a struct (or export) that was READ keeps
/// the header it was read with and writes it verbatim; only one without (a copy, or one whose
/// header was dropped) gets a header generated from the zero flags. So when an edit changes
/// whether a field is zero, the flag is recomputed AND the owner's stored header dropped;
/// when it does not, nothing is touched and the original header stays exact.
/// </summary>
public static class UnversionedZero
{
    /// <summary>What the field's zero flag should be for the value it holds, or null for kinds left alone.</summary>
    public static bool? Computed(PropertyData p) => p switch
    {
        IntPropertyData i => i.Value == 0,
        UInt32PropertyData u => u.Value == 0,
        BoolPropertyData b => !b.Value,
        FloatPropertyData f => f.Value == 0,
        ArrayPropertyData a => a.Value is null || a.Value.Length == 0,
        ObjectPropertyData o => o.Value is null || o.Value.Index == 0,
        NamePropertyData n => n.Value is null || n.Value.ToString() == "None",
        _ => null,
    };

    /// <summary>Recompute one field's flag; true when it changed (the owner's header is then stale).</summary>
    public static bool Refresh(PropertyData p)
    {
        if (Computed(p) is not { } z || p.IsZero == z) return false;
        p.IsZero = z;
        return true;
    }

    /// <summary>
    /// A struct and everything in it, for a copy whose fields may all have been edited. Any
    /// struct whose fields changed zero-ness loses its stored header, so one is generated.
    /// </summary>
    public static void RefreshAll(StructPropertyData s)
    {
        var changed = false;
        foreach (var p in s.Value)
        {
            if (p is StructPropertyData inner) RefreshAll(inner);
            else if (p is ArrayPropertyData a)
                foreach (var e in a.Value.OfType<StructPropertyData>()) RefreshAll(e);
            changed |= Refresh(p);
        }
        if (changed) s._originalStructHeader = null;
    }

    /// <summary>A field of an export's top level: recompute, and drop the export's header if it changed.</summary>
    public static void RefreshTop(NormalExport export, PropertyData p)
    {
        if (Refresh(p)) export.OriginalUnversionedHeader = null;
    }

    /// <summary>A field directly inside a struct that was read: recompute, and drop that struct's header if it changed.</summary>
    public static void RefreshIn(StructPropertyData owner, PropertyData p)
    {
        if (Refresh(p)) owner._originalStructHeader = null;
    }
}
