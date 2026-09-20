// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Infrastructure.Sync;

/// <summary>
/// One authenticated connection to the hub: the HTTP client and the credential it carries.
/// </summary>
/// <remarks>
/// Exists so that every hub call a terminal makes shares a single client and a single
/// endpoint. If the transport resolved its own client and the store directory resolved
/// another, the two could disagree about which store the terminal is — and a transfer would
/// then be addressed from a store the hub never issued the credential for.
/// </remarks>
/// <param name="Http">Client with the terminal's credential already attached.</param>
/// <param name="Endpoint">Where the hub lives and who this terminal is.</param>
public sealed record SyncConnection(HttpClient Http, SyncEndpoint Endpoint);
