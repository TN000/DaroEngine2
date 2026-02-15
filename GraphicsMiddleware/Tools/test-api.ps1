# ============================================================================
# Graphics Middleware REST API Test Script
# Usage: .\test-api.ps1 [-ScenePath "D:\path\to\scene.daro"]
# ============================================================================

param(
    [string]$ScenePath = "",
    [string]$BaseUrl = "http://localhost:5000"
)

Write-Host "=== Graphics Middleware API Test ===" -ForegroundColor Cyan
Write-Host ""

# Test 1: Health Check
Write-Host "1. Health Check" -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
    Write-Host "   Status: OK" -ForegroundColor Green
}
catch {
    Write-Host "   Status: FAILED - $_" -ForegroundColor Red
    exit 1
}

# Test 2: Engine Status
Write-Host ""
Write-Host "2. Engine Status" -ForegroundColor Yellow
try {
    $status = Invoke-RestMethod -Uri "$BaseUrl/api/control/status" -Method Get
    Write-Host "   State: $($status.state)" -ForegroundColor Green
    Write-Host "   Initialized: $($status.isInitialized)" -ForegroundColor $(if($status.isInitialized){"Green"}else{"Yellow"})
    Write-Host "   FPS: $($status.fps)" -ForegroundColor DarkGray
}
catch {
    Write-Host "   FAILED: $_" -ForegroundColor Red
}

# Test 3: Create Playlist Item (if scene path provided)
if ($ScenePath -and (Test-Path $ScenePath)) {
    Write-Host ""
    Write-Host "3. Create Playlist Item" -ForegroundColor Yellow

    $itemData = @{
        linkedScenePath = $ScenePath
        name = "Test Item"
        filledData = @{
            "test_field" = "Test Value"
        }
    } | ConvertTo-Json -Depth 3

    try {
        $createResult = Invoke-RestMethod -Uri "$BaseUrl/api/items" -Method Post -Body $itemData -ContentType "application/json"
        $itemId = $createResult.id
        Write-Host "   Created item ID: $itemId" -ForegroundColor Green
    }
    catch {
        Write-Host "   FAILED: $_" -ForegroundColor Red
        $itemId = $null
    }

    # Test 4: Get Item
    if ($itemId) {
        Write-Host ""
        Write-Host "4. Get Playlist Item" -ForegroundColor Yellow
        try {
            $item = Invoke-RestMethod -Uri "$BaseUrl/api/items/$itemId" -Method Get
            Write-Host "   Retrieved: $($item.id)" -ForegroundColor Green
            Write-Host "   Scene: $($item.linkedScenePath)" -ForegroundColor DarkGray
        }
        catch {
            Write-Host "   FAILED: $_" -ForegroundColor Red
        }
    }

    # Test 5: Direct CUE
    Write-Host ""
    Write-Host "5. Direct CUE" -ForegroundColor Yellow
    try {
        $cueData = @{
            scenePath = $ScenePath
        } | ConvertTo-Json

        $cueResult = Invoke-RestMethod -Uri "$BaseUrl/api/control/cue" -Method Post -Body $cueData -ContentType "application/json"
        Write-Host "   CUE successful: $cueResult" -ForegroundColor Green
    }
    catch {
        Write-Host "   FAILED: $_" -ForegroundColor Red
    }

    # Test 6: Engine Status after CUE
    Write-Host ""
    Write-Host "6. Engine Status (after CUE)" -ForegroundColor Yellow
    try {
        $status = Invoke-RestMethod -Uri "$BaseUrl/api/control/status" -Method Get
        Write-Host "   State: $($status.state)" -ForegroundColor Green
        Write-Host "   Current Item: $($status.currentItemId)" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "   FAILED: $_" -ForegroundColor Red
    }
}
else {
    Write-Host ""
    Write-Host "3-6. Skipped (no scene path provided or file not found)" -ForegroundColor Yellow
    Write-Host "   Use: .\test-api.ps1 -ScenePath 'D:\path\to\scene.daro'" -ForegroundColor DarkGray
}

# Test 7: List Items
Write-Host ""
Write-Host "7. List Playlist Items" -ForegroundColor Yellow
try {
    $items = Invoke-RestMethod -Uri "$BaseUrl/api/items" -Method Get
    Write-Host "   Total items: $($items.Count)" -ForegroundColor Green
    foreach ($i in $items | Select-Object -First 3) {
        Write-Host "   - $($i.id): $($i.name)" -ForegroundColor DarkGray
    }
}
catch {
    Write-Host "   FAILED: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Test Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "To test Mosart TCP commands, use:" -ForegroundColor Yellow
Write-Host "  .\test-mosart.ps1" -ForegroundColor White
Write-Host ""
Write-Host "Mosart protocol:" -ForegroundColor Yellow
Write-Host "  GUID|0  - CUE (load item)" -ForegroundColor White
Write-Host "  GUID|1  - PLAY" -ForegroundColor White
Write-Host "  GUID|2  - STOP" -ForegroundColor White
Write-Host "  GUID|3  - CONTINUE" -ForegroundColor White
Write-Host "  GUID|4  - PAUSE" -ForegroundColor White
