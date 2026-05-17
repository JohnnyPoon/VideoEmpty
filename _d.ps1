$A = 'D:\CapCut\User Data\Projects\com.lveditor.draft\0512 (2) (VideoEmpty 2026-05-17 09-17-55)\draft_content.json'
$B = 'D:\CapCut\User Data\Projects\com.lveditor.draft\0512 (2) (VideoEmpty 2026-05-17 09-17-55) - Copy\draft_content.json'
$a = Get-Content $A -Raw | ConvertFrom-Json -Depth 64
$b = Get-Content $B -Raw | ConvertFrom-Json -Depth 64
# Item 1 (Step from Left) starts at 0,  Items 2/3 starts at 4466666 and 11900000
# Find shape segments starting at matching times.
$times = @(0, 4466666, 11900000)
foreach ($t in $times) {
  "=== shape segments at start=$t ==="
  foreach ($doc in @(@{n="A";d=$a}, @{n="B";d=$b})) {
    foreach ($tr in $doc.d.tracks) {
      if ($tr.type -ne "sticker") { continue }
      foreach ($s in $tr.segments) {
        if ($s.target_timerange.start -eq $t) {
          $mat = $doc.d.materials.shapes | Where-Object { $_.id -eq $s.material_id } | Select-Object -First 1
          if (-not $mat) { continue }
          $sz = $mat.shape_size -join ","
          "  $($doc.n)  matId=$($mat.id)  size=[$sz]  fill=$($mat.shape_fill_color -join ',')  border=$($mat.shape_border_color -join ',')  borderW=$($mat.shape_border_width)  segScale=$($s.uniform_scale.value)  tx=$($s.clip.transform.x)  ty=$($s.clip.transform.y)"
        }
      }
    }
  }
}
