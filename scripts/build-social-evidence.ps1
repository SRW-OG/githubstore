param([string]$ResearchDirectory = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Temp\githubstore-curation-20261020'))
$ErrorActionPreference='Stop'
$destination=Join-Path $PSScriptRoot '..\data\curation\sources-social.json'
$mapping=@{
 '6aaa55ec000000001001e66a'=@('ComfyUI','Ollama','Open WebUI','Excalidraw','RustDesk','Godot','Supabase')
 '6aabb101000000000d0267be'=@('HandBrake','Everything Toolbar','SumatraPDF','ScreenToGif','Motrix')
 'BV1tu4y1n7uX'=@('GIMP','Natron','OpenShot','Inkscape','digiKam')
 'BV1URruYHEut'=@('小幻阅读','Reader Desktop','Koodo Reader','Readest','Calibre')
 'BV1vWg36QExc'=@('LibreOffice','VLC','DaVinci Resolve','OBS Studio','GIMP','Audacity','Blender','Obsidian','Scratch','Godot')
}
$sources=@()
$insufficient=@()
foreach($fileName in @('xhs-details.json','bilibili-details.json')) {
 $path=Join-Path $researchDirectory $fileName
 $retrieved=(Get-Item -LiteralPath $path).LastWriteTimeUtc.ToString('O')
 $platform=if($fileName.StartsWith('xhs')){'xiaohongshu'}else{'bilibili'}
 foreach($item in (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)) {
  if(-not $mapping.ContainsKey($item.id)) {
   $insufficient += [ordered]@{platform=$platform;title=$item.title;url=$item.url;reason='正文只有话题或没有可核对的软件名称，未将图片标题推断为具体软件推荐。'}
   continue
  }
  $mentions=@()
  foreach($name in $mapping[$item.id]) {
   $match=[regex]::Match($item.text,[regex]::Escape($name),[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
   if(-not $match.Success){continue}
   $mentions += [ordered]@{name=$name;quote=$match.Value;repo=$null}
  }
  $sources += [ordered]@{platform=$platform;title=[System.Net.WebUtility]::HtmlDecode($item.title);url=$item.url;retrievedAt=$retrieved;evidenceLevel='platform-text';softwareMentions=$mentions;limitations=@('证据为平台接口返回的笔记正文或视频简介；未观看视频或读取图片中的文字。','原帖的开源、性能、体积与完全替代宣传未当作事实；逐项另查官方仓库和许可证。')}
 }
}
$document=[ordered]@{schemaVersion=1;method='catbus 0.1.0 item search + item get';retrievedAt=[DateTimeOffset]::Now.ToString('O');sources=$sources;insufficientText=$insufficient;globalLimitations=@('只读采集，未执行点赞/收藏/评论/关注/发布。','小红书访问令牌未写入交付文件；来源为去查询参数的公开笔记链接。','来源混入免费闭源和非桌面工具；这些候选只进入审查表，不自动进入开源种子库。')}
$null=New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
$document | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $destination -Encoding utf8
[pscustomobject]@{sources=$sources.Count;mentions=($sources.softwareMentions | Measure-Object).Count;insufficientText=$insufficient.Count;output=$destination}|ConvertTo-Json
