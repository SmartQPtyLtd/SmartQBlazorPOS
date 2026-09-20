// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Devices.EscPos;

/// <summary>ESC a n — horizontal justification.</summary>
public enum TextAlignment : byte
{
    Left = 0,
    Centre = 1,
    Right = 2,
}

/// <summary>GS V m — paper cut style.</summary>
public enum PaperCut : byte
{
    /// <summary>Cuts all the way through. Standard for a customer receipt.</summary>
    Full = 0,

    /// <summary>Leaves a small tab so the slip stays attached. Used for kitchen tickets.</summary>
    Partial = 1,
}

/// <summary>ESC p m — which cash-drawer connector to pulse.</summary>
public enum DrawerPin : byte
{
    /// <summary>Pin 2. The default on essentially all drawer cables.</summary>
    Pin2 = 0,

    /// <summary>Pin 5. Used by some dual-drawer setups.</summary>
    Pin5 = 1,
}

/// <summary>GS ( k — QR error correction level. Higher levels survive smudging but make the symbol denser.</summary>
public enum QrErrorCorrection : byte
{
    /// <summary>Recovers ~7% damage.</summary>
    Low = 48,

    /// <summary>Recovers ~15% damage. A good default for a receipt that may get creased.</summary>
    Medium = 49,

    /// <summary>Recovers ~25% damage.</summary>
    Quartile = 50,

    /// <summary>Recovers ~30% damage.</summary>
    High = 51,
}

/// <summary>GS k m — 1D barcode symbology.</summary>
public enum BarcodeSymbology
{
    /// <summary>General-purpose alphanumeric. The safe default for internal references.</summary>
    Code128,

    /// <summary>Alphanumeric, larger symbols. Common on retail shelf labels.</summary>
    Code39,

    /// <summary>Retail product code, 13 digits.</summary>
    Ean13,

    /// <summary>Short retail product code, 8 digits.</summary>
    Ean8,

    /// <summary>North American retail product code, 12 digits.</summary>
    UpcA,

    /// <summary>Interleaved 2 of 5, even-digit numeric. Used on some cartons.</summary>
    Itf,
}

/// <summary>GS H n — placement of the human-readable barcode text.</summary>
public enum BarcodeTextPosition : byte
{
    NotPrinted = 0,
    Above = 1,
    Below = 2,
    Both = 3,
}
