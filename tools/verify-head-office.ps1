# Verifies the multi-store loop against a real running hub over HTTP.
#
# Companion to the sync transcript in PLAN.md: that one proved a replayed push cannot
# double-post a sale. This one proves the estate boundary -- that head-office operations are
# closed to a device credential, that two stores can trade independently, and that head office
# can see both and publish a catalogue back down to one.
#
# Run from the repository root:  pwsh -File tools/verify-head-office.ps1

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$port = 5199
$base = "http://127.0.0.1:$port"
$token = 'verification-head-office-token-0000000001'
$dbPath = Join-Path ([System.IO.Path]::GetTempPath()) "pos-verify-$(New-Guid).db"

$env:HeadOffice__Token = $token
$env:Sync__DatabasePath = $dbPath
$env:ASPNETCORE_URLS = $base
$env:ASPNETCORE_ENVIRONMENT = 'Production'

$step = 0
function Show([string]$label, [string]$detail) {
    $script:step++
    Write-Output ("{0,2}. {1,-34} {2}" -f $script:step, $label, $detail)
}

function Try-Call {
    param([scriptblock]$Call)
    try { return & $Call } catch { return $null }
}

$server = Start-Process -FilePath 'dotnet' `
    -ArgumentList @(
        'run', '--project', (Join-Path $repo 'src/Pos.Sync.Server/Pos.Sync.Server.csproj'),
        '-c', 'Release', '--no-build',
        # Without this the launch profile wins and the hub binds to its development port instead,
        # leaving this script waiting for an address nothing is listening on.
        '--no-launch-profile') `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $env:TEMP 'pos-verify-hub.out.log') `
    -RedirectStandardError (Join-Path $env:TEMP 'pos-verify-hub.err.log')

try {
    # Wait for the hub to answer.
    $ready = $false
    foreach ($i in 1..60) {
        Start-Sleep -Milliseconds 500
        $health = Try-Call { Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 2 }
        if ($health -and $health.StatusCode -eq 200) { $ready = $true; break }
    }

    if (-not $ready) {
        $err = Try-Call { Get-Content (Join-Path $env:TEMP 'pos-verify-hub.err.log') -Raw }
        throw "The hub did not start on $base. stderr: $err"
    }

    Show 'health' "HTTP 200: $($health.Content)"

    $head = @{ 'X-Pos-HeadOffice' = $token }

    # --- the estate boundary ------------------------------------------------------

    $noToken = Try-Call {
        Invoke-WebRequest "$base/api/enrollment/stores" -Method POST -UseBasicParsing `
            -ContentType 'application/json' -TimeoutSec 10 `
            -Body '{"code":"XX01","name":"Should not exist"}'
    }
    if ($noToken) { throw 'A store was provisioned with no head-office token.' }
    Show 'provision with no token' 'refused (as it must be)'

    $wrongToken = Try-Call {
        Invoke-WebRequest "$base/api/enrollment/stores" -Method POST -UseBasicParsing `
            -ContentType 'application/json' -TimeoutSec 10 `
            -Headers @{ 'X-Pos-HeadOffice' = 'not-the-right-token-at-all-000000' } `
            -Body '{"code":"XX02","name":"Should not exist either"}'
    }
    if ($wrongToken) { throw 'A store was provisioned with a wrong head-office token.' }
    Show 'provision with a wrong token' 'refused (as it must be)'

    $devicesNoToken = Try-Call {
        Invoke-WebRequest "$base/api/devices" -UseBasicParsing -TimeoutSec 10
    }
    if ($devicesNoToken) { throw 'The device register was readable with no head-office token.' }
    Show 'device register with no token' 'refused (as it must be)'

    # --- stand up two stores ------------------------------------------------------

    $storeA = Invoke-RestMethod "$base/api/enrollment/stores" -Method POST -Headers $head `
        -ContentType 'application/json' -TimeoutSec 10 `
        -Body '{"code":"CT01","name":"Cape Town","currency":"ZAR","taxMode":"Inclusive","defaultTaxRate":0.15}'

    $storeB = Invoke-RestMethod "$base/api/enrollment/stores" -Method POST -Headers $head `
        -ContentType 'application/json' -TimeoutSec 10 `
        -Body '{"code":"JN01","name":"Johannesburg","currency":"ZAR","taxMode":"Inclusive","defaultTaxRate":0.15}'

    Show 'provision two stores' "CT01=$($storeA.storeId.Substring(0,8))... JN01=$($storeB.storeId.Substring(0,8))..."

    # --- enrol a till in each -----------------------------------------------------

    function NewCredential {
        return [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).
            Replace('+', '-').Replace('/', '_').TrimEnd('=')
    }

    function Enroll([string]$code, [string]$label) {
        $secret = NewCredential
        $refresh = NewCredential

        $body = @{
            code = $code; label = $label; deviceSecret = $secret; deviceRefreshToken = $refresh
        } | ConvertTo-Json -Compress

        $result = Invoke-RestMethod "$base/api/enrollment/enroll" -Method POST `
            -ContentType 'application/json' -TimeoutSec 10 -Body $body

        return @{ Response = $result; Secret = $secret; Refresh = $refresh }
    }

    $tillA = Enroll $storeA.enrollmentCode 'Front counter 1'
    $tillB = Enroll $storeB.enrollmentCode 'Front counter 1'

    Show 'enroll a till per store' "CT01 till=$($tillA.Response.deviceId.Substring(0,8))... JN01 till=$($tillB.Response.deviceId.Substring(0,8))..."

    $reused = Try-Call {
        $body = @{ code = $storeA.enrollmentCode; label = 'Replay'; deviceSecret = ('x' * 40) } | ConvertTo-Json -Compress
        Invoke-WebRequest "$base/api/enrollment/enroll" -Method POST -ContentType 'application/json' `
            -TimeoutSec 10 -Body $body
    }
    if ($reused) { throw 'A consumed enrolment code was accepted a second time.' }
    Show 'reuse a consumed code' 'refused (as it must be)'

    # An enrolment that asks for no refresh token would leave a till that can never rotate its
    # credential, which is the whole point of issuing one. The code is minted fresh and left unused so
    # that the refusal can only be about the missing token.
    $spareCode = (Invoke-RestMethod "$base/api/enrollment/codes/$($storeA.storeId)" -Method POST `
        -Headers $head -TimeoutSec 10).enrollmentCode

    $noRefresh = Try-Call {
        $body = @{ code = $spareCode; label = 'No refresh'; deviceSecret = ('y' * 40) } | ConvertTo-Json -Compress
        Invoke-WebRequest "$base/api/enrollment/enroll" -Method POST -ContentType 'application/json' `
            -TimeoutSec 10 -Body $body
    }
    if ($noRefresh) { throw 'A device was enrolled without a refresh token.' }

    # And the same code still works once a refresh token is supplied, which proves the refusal above was
    # about the token rather than about the code.
    $withRefresh = Invoke-RestMethod "$base/api/enrollment/enroll" -Method POST `
        -ContentType 'application/json' -TimeoutSec 10 `
        -Body (@{ code = $spareCode; label = 'Complete'; deviceSecret = ('z' * 40)
            deviceRefreshToken = ('w' * 40) } | ConvertTo-Json -Compress)

    if (-not $withRefresh.rotateAfter) { throw 'Enrolment did not say when the credential falls due.' }

    Show 'enrol without a refresh token' 'refused; the same code then works with one'

    # --- each till pushes its own trading day -------------------------------------

    function PushSale($till, $storeId, [string]$saleId, [long]$seq, [string]$date, [double]$total, [double]$tax) {
        $payload = @{
            id = $saleId; storeId = $storeId; number = "T-$seq"; terminalSeq = $seq
            completedAt = "${date}T12:00:00.0000000+00:00"; businessDate = $date; localHour = 12
            currency = 'ZAR'; taxMode = 'Inclusive'; status = 'Completed'
            lines = @(); tenders = @()
            subtotal = $total; totalDiscount = 0; taxTotal = $tax; total = $total
        } | ConvertTo-Json -Compress -Depth 5

        $body = @{
            batchId = [Guid]::NewGuid().ToString('N')
            records = @(@{
                entityType = 'sale'; entityId = $saleId; terminalId = $till.Response.deviceId
                terminalSeq = $seq; payload = $payload
            })
        } | ConvertTo-Json -Compress -Depth 6

        return Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
            -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($till.secret)" } -Body $body
    }

    $dateA = '2026-03-25'
    $dateB = '2026-03-25'

    $saleA1 = [Guid]::NewGuid().ToString('N')
    $saleA2 = [Guid]::NewGuid().ToString('N')
    $saleB1 = [Guid]::NewGuid().ToString('N')

    $pushA1 = PushSale $tillA $storeA.storeId $saleA1 1 $dateA 115.00 15.00
    $pushA2 = PushSale $tillA $storeA.storeId $saleA2 2 $dateA 230.00 30.00
    $pushB1 = PushSale $tillB $storeB.storeId $saleB1 1 $dateB 500.00 65.22

    Show 'CT01 pushes two sales' "statuses=$($pushA1.results[0].status),$($pushA2.results[0].status) cursor=$($pushA2.cursor)"
    Show 'JN01 pushes one sale' "status=$($pushB1.results[0].status) cursor=$($pushB1.cursor)"

    # A sale voided before any money changed hands. It must be counted and must not be added
    # to the estate's takings.
    $saleA3 = [Guid]::NewGuid().ToString('N')
    $payloadVoid = @{
        id = $saleA3; storeId = $storeA.storeId; number = 'T-3'; terminalSeq = 3
        completedAt = "${dateA}T12:30:00.0000000+00:00"; businessDate = $dateA; localHour = 12
        currency = 'ZAR'; taxMode = 'Inclusive'; status = 'Voided'
        lines = @(); tenders = @()
        subtotal = 999; totalDiscount = 0; taxTotal = 130.30; total = 999.00
    } | ConvertTo-Json -Compress -Depth 5

    $voidBody = @{
        batchId = [Guid]::NewGuid().ToString('N')
        records = @(@{ entityType = 'sale'; entityId = $saleA3; terminalId = $tillA.Response.deviceId
            terminalSeq = 3; payload = $payloadVoid })
    } | ConvertTo-Json -Compress -Depth 6

    $pushVoid = Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
        -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($tillA.Secret)" } -Body $voidBody

    Show 'CT01 pushes a voided sale' "status=$($pushVoid.results[0].status)"

    # A refund, which must reduce net takings.
    $refundId = [Guid]::NewGuid().ToString('N')
    $payloadRefund = @{
        id = $refundId; storeId = $storeA.storeId; originalSaleId = $saleA1
        originalSaleNumber = 'T-1'; number = 'R-T-1'; terminalSeq = 4
        completedAt = "${dateA}T13:00:00.0000000+00:00"; businessDate = $dateA; localHour = 13
        currency = 'ZAR'; lines = @(); refunds = @(); reason = 'Faulty'
        totalRefund = 50.00; taxReversed = 6.52
    } | ConvertTo-Json -Compress -Depth 5

    $refundBody = @{
        batchId = [Guid]::NewGuid().ToString('N')
        records = @(@{ entityType = 'salesReturn'; entityId = $refundId; terminalId = $tillA.Response.deviceId
            terminalSeq = 4; payload = $payloadRefund })
    } | ConvertTo-Json -Compress -Depth 6

    $pushRefund = Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
        -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($tillA.Secret)" } -Body $refundBody

    Show 'CT01 pushes a refund' "status=$($pushRefund.results[0].status)"

    # A malformed record must be refused on its own, not take the batch down with it. A terminal
    # that could not push a day's trading because one bad record rode along with it would have no
    # way to recover without intervention.
    $malformedBody = @{
        batchId = [Guid]::NewGuid().ToString('N')
        records = @(
            @{ entityType = 'sale'; entityId = [Guid]::NewGuid().ToString('N')
               terminalId = ''; terminalSeq = 5; payload = '{}' }
        )
    } | ConvertTo-Json -Compress -Depth 6

    $malformed = Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
        -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($tillA.Secret)" } `
        -Body $malformedBody

    if ($malformed.results[0].status -ne 2) {
        throw "A record with no terminalId was not rejected; status was $($malformed.results[0].status)."
    }
    Show 'push a record with no terminal' "rejected: $($malformed.results[0].error)"

    # --- head office sees the whole estate ---------------------------------------

    $stores = Invoke-RestMethod "$base/api/head-office/stores" -Headers $head -TimeoutSec 10
    Show 'store register' "$($stores.Count) stores; tills=$((($stores | ForEach-Object { $_.activeDeviceCount }) -join ','))"

    $report = Invoke-RestMethod "$base/api/head-office/reports/consolidated?from=$dateA&to=$dateA" `
        -Headers $head -TimeoutSec 20

    Show 'consolidated report' "gross=$($report.gross) net takings=$($report.netTakings) tax=$($report.tax)"

    foreach ($s in $report.rankedByTakings) {
        Show "  $($s.storeCode) $($s.storeName)" `
            "sales=$($s.saleCount) voids=$($s.voidCount) gross=$($s.gross) refunds=$($s.refundTotal) net=$($s.netTakings)"
    }

    if ([decimal]$report.gross -ne 845.00) { throw "Expected gross 845.00, got $($report.gross)." }
    if ([decimal]$report.refundTotal -ne 50.00) { throw "Expected refunds 50.00, got $($report.refundTotal)." }
    if ([decimal]$report.netTakings -ne 795.00) { throw "Expected net takings 795.00, got $($report.netTakings)." }
    if ([int]$report.saleCount -ne 3) { throw "Expected 3 sales, got $($report.saleCount)." }
    if ([int]$report.voidCount -ne 1) { throw "Expected 1 void, got $($report.voidCount)." }

    $capeTown = $report.stores | Where-Object { $_.storeCode -eq 'CT01' }
    if ([decimal]$capeTown.voidedValue -ne 999.00) { throw "Voided value not reported separately." }

    Show 'assertions' 'gross, refunds, net takings, sale count, void count all as expected'

    # A period that ends before it starts is a caller mistake, not an empty report.
    $badPeriod = Try-Call {
        Invoke-WebRequest "$base/api/head-office/reports/consolidated?from=2026-03-25&to=2026-03-01" `
            -Headers $head -UseBasicParsing -TimeoutSec 10
    }
    if ($badPeriod) { throw 'A backwards period was accepted.' }
    Show 'backwards period' 'refused (as it must be)'

    # --- head office publishes a catalogue down to one store ---------------------

    $productA = [Guid]::NewGuid().ToString('N')
    $productB = [Guid]::NewGuid().ToString('N')

    $publishBody = @{
        products = @(
            @{ id = $productA; barcode = '6001000000017'; name = 'Cola 500ml'; unitPrice = 15.00
               taxName = 'VAT'; taxRate = 0.15; stationId = 'bar' },
            @{ id = $productB; barcode = '6001000000024'; name = 'Still Water'; unitPrice = 12.50
               taxName = 'VAT'; taxRate = 0.15 },
            @{ id = [Guid]::NewGuid().ToString('N'); barcode = ''; name = 'No barcode'; unitPrice = 5.00
               taxName = 'VAT'; taxRate = 0.15 }
        )
    } | ConvertTo-Json -Compress -Depth 5

    $published = Invoke-RestMethod "$base/api/head-office/stores/$($storeA.storeId)/catalogue" `
        -Method POST -Headers $head -ContentType 'application/json' -TimeoutSec 10 -Body $publishBody

    Show 'publish catalogue to CT01' "published=$($published.published) rejected=$($published.rejected.Count) ($($published.rejected -join '; '))"

    if ([int]$published.published -ne 2) { throw "Expected 2 published, got $($published.published)." }
    if ([int]$published.rejected.Count -ne 1) { throw 'The product with no barcode was not rejected.' }

    # Each till pulls the catalog stream and finds only its own store's catalogue.
    function Pull([string]$stream, $till) {
        return Invoke-RestMethod "$base/api/sync/pull?stream=$stream&cursor=0&limit=200" `
            -Headers @{ Authorization = "Bearer $($till.Secret)" } -TimeoutSec 15
    }

    $catalogA = Pull 'catalog' $tillA
    $catalogB = Pull 'catalog' $tillB

    Show 'CT01 pulls catalog' "$($catalogA.changes.Count) change(s)"
    Show 'JN01 pulls catalog' "$($catalogB.changes.Count) change(s) -- another store's catalogue is not visible"

    if ([int]$catalogA.changes.Count -ne 2) { throw "CT01 saw $($catalogA.changes.Count) catalogue changes, expected 2." }
    if ([int]$catalogB.changes.Count -ne 0) { throw "JN01 saw $($catalogB.changes.Count) catalogue changes, expected none." }

    $first = $catalogA.changes[0].payload | ConvertFrom-Json
    Show '  published product shape' "name=$($first.name) price=$($first.unitPrice) storeId=$($first.storeId.Substring(0,8))..."

    if (-not $first.storeId) { throw 'The published product carries no storeId, so the till cannot store it.' }
    if ($first.name -notin @('Cola 500ml', 'Still Water')) { throw "Unexpected product name '$($first.name)'." }

    # The station has to survive the wire, or a catalogue pull silently strips it from every
    # product and the kitchen stops receiving tickets with nothing to show for it. The payload
    # carries every field the terminal stores, so the applier never has to invent one.
    $withStation = $catalogA.changes |
        ForEach-Object { $_.payload | ConvertFrom-Json } |
        Where-Object { $_.id -eq $productA }

    if (-not $withStation) { throw 'The published product was not in the pulled changes.' }
    if ($withStation.stationId -ne 'bar') {
        throw "The station did not survive the wire: got '$($withStation.stationId)', expected 'bar'."
    }

    Show '  station survives publication' "stationId=$($withStation.stationId)"

    # Every field the terminal stores must be present in the payload. A field the applier does not
    # know about is a field the catalogue sync erases from every product in the shop.
    $requiredFields = @('id','storeId','barcode','name','unitPrice','taxName','taxRate',
                        'sku','isActive','isDeleted','isOpenPrice','isSoldByWeight','stationId')

    $missing = $requiredFields | Where-Object { -not $withStation.PSObject.Properties.Name.Contains($_) }

    if ($missing) { throw "The published payload omits: $($missing -join ', ')." }

    Show '  payload carries every field' "$($requiredFields.Count) fields present"

    # --- a transfer crosses the store boundary ------------------------------------

    # CT01 dispatches stock to JN01. The document is authored by CT01 and addressed to JN01, so the
    # hub has to relay it — JN01's pull only returns JN01's own changes, and without the relay the
    # goods would sit in transit forever with the receiving shop never knowing.
    # The store ids here are deliberately written the way a real till writes them — as dashed GUIDs,
    # because that is what the domain type's ToString() produces — while the hub mints them without
    # dashes. Using the hub's own spelling in this payload is what let a broken relay pass
    # verification: the ids matched by luck, and in a shop they never would.
    $transferId = [Guid]::NewGuid().ToString('N')
    $transferPayload = @{
        id = $transferId
        fromStoreId = ([Guid]$storeA.storeId).ToString('D')
        toStoreId = ([Guid]$storeB.storeId).ToString('D')
        reference = 'TR-CT01-JN01-0001'
        status = 'Dispatched'
        createdAt = "${dateA}T15:00:00.0000000+00:00"
        createdByEmployeeId = 'emp-1'
        lines = @(@{ productId = 'p-transfer'; barcode = '6001000000017'; name = 'Cola 500ml'
                     quantitySent = 24 })
    } | ConvertTo-Json -Compress -Depth 6

    $transferBody = @{
        batchId = [Guid]::NewGuid().ToString('N')
        records = @(@{ entityType = 'stockTransfer'; entityId = $transferId
            terminalId = $tillA.Response.deviceId; terminalSeq = 50; payload = $transferPayload })
    } | ConvertTo-Json -Compress -Depth 6

    $pushTransfer = Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
        -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($tillA.Secret)" } `
        -Body $transferBody

    Show 'CT01 dispatches a transfer' "status=$($pushTransfer.results[0].status)"

    $salesA = Pull 'sales' $tillA
    $salesB = Pull 'sales' $tillB

    $seesA = @($salesA.changes | Where-Object { $_.entityId -eq $transferId }).Count
    $seesB = @($salesB.changes | Where-Object { $_.entityId -eq $transferId }).Count

    if ($seesA -ne 1) { throw "CT01 should see its own transfer once, saw $seesA." }
    if ($seesB -ne 1) { throw "JN01 should have been relayed the transfer, saw $seesB." }

    Show 'both stores see the transfer' "CT01=$seesA JN01=$seesB"

    $relayed = $salesB.changes | Where-Object { $_.entityId -eq $transferId } | Select-Object -First 1
    $relayedPayload = $relayed.payload | ConvertFrom-Json

    # Compared as identifiers rather than as text, on both the payload and the change-log row. The
    # payload legitimately keeps the sending till's spelling; the row must carry the hub's, because
    # that is what JN01's pull is scoped by.
    if ([Guid]$relayedPayload.toStoreId -ne [Guid]$storeB.storeId) {
        throw 'The relayed transfer is not addressed to JN01.'
    }
    if ([Guid]$relayed.storeId -ne [Guid]$storeB.storeId) {
        throw "The relayed change is scoped to '$($relayed.storeId)', not JN01."
    }
    if ([decimal]$relayedPayload.lines[0].quantitySent -ne 24) {
        throw 'The relayed transfer lost its lines.'
    }

    Show '  relayed intact' "to=$($relayedPayload.toStoreId.Substring(0,8))... qty=$($relayedPayload.lines[0].quantitySent)"

    # A transfer addressed to a store the hub does not know is stored but not relayed: refusing it
    # would strand the record while the stock had already left the sending shop's books.
    $orphanId = [Guid]::NewGuid().ToString('N')
    $orphanPayload = $transferPayload | ConvertFrom-Json
    $orphanPayload.id = $orphanId
    $orphanPayload.toStoreId = 'no-such-store'

    $orphanBody = @{
        batchId = [Guid]::NewGuid().ToString('N')
        records = @(@{ entityType = 'stockTransfer'; entityId = $orphanId
            terminalId = $tillA.Response.deviceId; terminalSeq = 51
            payload = ($orphanPayload | ConvertTo-Json -Compress -Depth 6) })
    } | ConvertTo-Json -Compress -Depth 6

    $pushOrphan = Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
        -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($tillA.Secret)" } `
        -Body $orphanBody

    if ($pushOrphan.results[0].status -ne 0) {
        throw "A transfer to an unknown store was not accepted; status $($pushOrphan.results[0].status)."
    }

    $salesAAfter = Pull 'sales' $tillA
    $seesOrphan = @($salesAAfter.changes | Where-Object { $_.entityId -eq $orphanId }).Count

    if ($seesOrphan -ne 1) { throw "The unrelayable transfer should still reach its author, saw $seesOrphan." }

    Show 'transfer to an unknown store' 'stored for its author, not relayed'

    # A device credential must not reach head-office endpoints even though it is valid.
    $deviceOnHeadOffice = Try-Call {
        Invoke-WebRequest "$base/api/head-office/stores" -UseBasicParsing -TimeoutSec 10 `
            -Headers @{ Authorization = "Bearer $($tillA.Secret)" }
    }
    if ($deviceOnHeadOffice) { throw 'A device credential reached the head-office store register.' }
    Show 'device credential on head office' 'refused (as it must be)'

    # --- a till cannot write into another store's books ---------------------------

    $crossStore = Try-Call {
        $payload = @{
            id = [Guid]::NewGuid().ToString('N'); storeId = $storeB.storeId; number = 'X-1'; terminalSeq = 99
            completedAt = "${dateA}T14:00:00.0000000+00:00"; businessDate = $dateA; localHour = 14
            currency = 'ZAR'; taxMode = 'Inclusive'; status = 'Completed'
            lines = @(); tenders = @(); subtotal = 1; totalDiscount = 0; taxTotal = 0; total = 1
        } | ConvertTo-Json -Compress -Depth 5

        $body = @{
            batchId = [Guid]::NewGuid().ToString('N')
            records = @(@{ entityType = 'sale'; entityId = [Guid]::NewGuid().ToString('N')
                terminalId = $tillA.Response.deviceId; terminalSeq = 99; payload = $payload })
        } | ConvertTo-Json -Compress -Depth 6

        Invoke-RestMethod "$base/api/sync/push" -Method POST -TimeoutSec 15 `
            -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($tillA.Secret)" } -Body $body
    }

    $reportAfter = Invoke-RestMethod "$base/api/head-office/reports/consolidated?from=$dateA&to=$dateA" `
        -Headers $head -TimeoutSec 20

    $jhb = $reportAfter.stores | Where-Object { $_.storeCode -eq 'JN01' }
    if ([int]$jhb.saleCount -ne 1) {
        throw "A till in CT01 wrote into JN01's books: JN01 now shows $($jhb.saleCount) sales."
    }

    Show 'cross-store push' "stored against CT01, not JN01 -- JN01 still shows $($jhb.saleCount) sale"

    # --- a till can name the branches it may send stock to ------------------------

    # The store directory is what makes the transfers screen usable: a shop cannot address a transfer
    # to a branch it cannot name, and it must not be handed the head-office token to find out.
    $directory = Invoke-RestMethod "$base/api/sync/stores" -TimeoutSec 10 `
        -Headers @{ Authorization = "Bearer $($tillA.Secret)" }

    $codes = @($directory.stores | ForEach-Object { $_.code })

    Show 'till reads the store directory' "$($directory.stores.Count) store(s): $($codes -join ', ')"

    if ($codes -notcontains 'CT01' -or $codes -notcontains 'JN01') {
        throw "The store directory is missing a branch: got $($codes -join ', ')."
    }
    if ([Guid]$directory.storeId -ne [Guid]$storeA.storeId) {
        throw 'The directory did not report which store the caller is.'
    }

    # The projection is the point. Currency, tax mode and registration numbers are the estate's
    # configuration and belong behind the head-office token, not in a browser in a shop.
    $leaked = $directory.stores[0].PSObject.Properties.Name |
        Where-Object { $_ -notin @('id', 'code', 'name', 'isActive') }

    if ($leaked) { throw "The store directory exposes: $($leaked -join ', ')." }

    Show '  directory carries only names' 'id, code, name, isActive -- no tax or currency'

    # Anonymous callers get nothing, exactly as the rest of the sync surface requires.
    $anonymousDirectory = Try-Call {
        Invoke-WebRequest "$base/api/sync/stores" -UseBasicParsing -TimeoutSec 10
    }
    if ($anonymousDirectory) { throw 'The store directory was readable without a credential.' }
    Show '  directory without a credential' 'refused (as it must be)'

    # --- rotating a device credential ---------------------------------------------
    #
    # A credential that can never change is valid forever once it leaks, and the secret travels on every
    # sync request. These steps are the live version of the rules in DeviceRotation: the replacement is
    # generated by the till, a lost response is retried with the same pair, and a superseded refresh
    # token used to mint *different* credentials revokes the device.
    #
    # Till B is the one that rotates and it ends up revoked, so nothing may use it afterwards.

    function Rotate([string]$secret, [string]$refresh, [string]$newSecret, [string]$newRefresh) {
        $body = @{ newSecret = $newSecret; newRefreshToken = $newRefresh } | ConvertTo-Json -Compress

        return Invoke-RestMethod "$base/api/devices/rotate" -Method POST -TimeoutSec 15 `
            -ContentType 'application/json' `
            -Headers @{ Authorization = "Bearer $secret"; 'X-Pos-Refresh' = $refresh } `
            -Body $body
    }

    $originalSecret = $tillB.Secret
    $originalRefresh = $tillB.Refresh
    $rotatedSecret = NewCredential
    $rotatedRefresh = NewCredential

    $rotation = Rotate $originalSecret $originalRefresh $rotatedSecret $rotatedRefresh

    if (-not $rotation.rotateAfter) { throw 'A rotation did not say when the next one falls due.' }
    Show 'till rotates its credential' "device=$($rotation.deviceId.Substring(0,8))... next=$($rotation.rotateAfter.ToString('yyyy-MM-dd'))"

    # The replacement works. Nothing about it came back from the hub, so if this fails the till is
    # holding a credential the hub never stored.
    $withNew = Invoke-RestMethod "$base/api/sync/stores" -TimeoutSec 10 `
        -Headers @{ Authorization = "Bearer $rotatedSecret" }
    if ([Guid]$withNew.storeId -ne [Guid]$storeB.storeId) { throw 'The replacement credential reads the wrong store.' }
    Show '  the replacement works' 'JN01 till reads its own store'

    # And the superseded one still does, inside the grace window. This is the property that stops a lost
    # response from locking a till out of its own books while it retries: the till has not confirmed the
    # rotation, so it is still presenting this one.
    $oldStillWorks = Try-Call {
        Invoke-RestMethod "$base/api/sync/stores" -TimeoutSec 10 `
            -Headers @{ Authorization = "Bearer $originalSecret" }
    }
    if (-not $oldStillWorks) { throw 'The superseded secret stopped working immediately, so a lost response would strand a till.' }
    Show '  superseded secret in grace' 'still reads the store'

    # A refresh token this device never held: refused, and the device keeps working.
    $unknownToken = Try-Call { Rotate $rotatedSecret (NewCredential) (NewCredential) (NewCredential) }
    if ($unknownToken) { throw 'A refresh token the device never held was accepted.' }

    $stillAlive = Invoke-RestMethod "$base/api/sync/stores" -TimeoutSec 10 `
        -Headers @{ Authorization = "Bearer $rotatedSecret" }
    if (-not $stillAlive) { throw 'An unknown refresh token revoked the device.' }
    Show '  unknown refresh token' 'refused, device untouched'

    # The lost-response path: the identical rotation submitted again, by a till that never got its
    # answer and is therefore still presenting the superseded secret and the pair it asked for.
    $retry = Rotate $originalSecret $originalRefresh $rotatedSecret $rotatedRefresh

    if (-not $retry.rotateAfter) { throw 'A retried rotation was not answered as a success.' }

    $afterRetry = Invoke-RestMethod "$base/api/sync/stores" -TimeoutSec 10 `
        -Headers @{ Authorization = "Bearer $rotatedSecret" }
    if ([Guid]$afterRetry.storeId -ne [Guid]$storeB.storeId) { throw 'A retried rotation revoked the device.' }
    Show '  identical retry' 'answered as the rotation it already applied'

    # Now the case rotation exists to catch: the same superseded token minting *different* credentials.
    # Two parties hold this device's credentials, and only one of them can be the till.
    $reuse = Try-Call { Rotate $originalSecret $originalRefresh (NewCredential) (NewCredential) }
    if ($reuse) { throw 'A superseded refresh token was allowed to mint a different credential.' }

    $afterReuse = Try-Call {
        Invoke-RestMethod "$base/api/sync/stores" -TimeoutSec 10 `
            -Headers @{ Authorization = "Bearer $rotatedSecret" }
    }
    if ($afterReuse) { throw 'The device still worked after a refresh-token reuse was detected.' }
    Show '  superseded token reused' 'refused, and the device is revoked'

    Write-Output ''
    Write-Output 'All multi-store checks passed.'
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 500

    # SQLite holds the file briefly; a failed cleanup must not fail the run.
    try { if (Test-Path $dbPath) { Remove-Item $dbPath -Force } } catch { }
    try { if (Test-Path "$dbPath-shm") { Remove-Item "$dbPath-shm" -Force } } catch { }
    try { if (Test-Path "$dbPath-wal") { Remove-Item "$dbPath-wal" -Force } } catch { }
}
