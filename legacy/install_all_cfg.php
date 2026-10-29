<?php
$remove = '
Get-ChildItem -Path C:\Artsoft\ARTSOFTServicos\ServicosXMLService -Filter "*.cfg" | ForEach-Object { .\ArtExecSvc ServicosXMLService\$_ -remove "Pervasive.SQL (transactional)" }
';
$install = '
 Get-ChildItem -Path C:\Artsoft\ARTSOFTServicos\ServicosXMLService -Filter "*.cfg" | ForEach-Object { .\ArtExecSvc ServicosXMLService\$_ -install "Pervasive.SQL (transactional)" }
';