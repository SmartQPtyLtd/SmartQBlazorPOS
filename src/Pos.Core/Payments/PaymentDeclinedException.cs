// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Payments;

/// <summary>
/// A payment was refused, so the sale was not completed.
/// </summary>
/// <remarks>
/// A distinct type rather than a plain <see cref="InvalidOperationException"/>, because the
/// handling is different. A refusal is an ordinary retail outcome — a card is declined, a gift
/// card is empty — and the operator retries with another method. A programming error is a fault.
/// Catching them together means one of the two is always handled wrongly.
/// </remarks>
public sealed class PaymentDeclinedException : Exception
{
    public PaymentDeclinedException()
        : this("The payment was declined.")
    {
    }

    public PaymentDeclinedException(string message)
        : base(message)
    {
    }

    public PaymentDeclinedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
