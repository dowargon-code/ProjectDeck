namespace ProjectDeck.Core.Tests;

/// <summary>テスト用のデータ</summary>
internal static class Samples
{
    /// <summary>仕様書 6.2 のサンプル（画像URLは実在する形に置き換え）</summary>
    public const string SpecDeck = """
        {
          "schema": "projectdeck/1",
          "revision": 43,
          "updated_by": "grok",
          "pages": [
            {
              "id": "a-mes",
              "name": "A社 MES",
              "color": "#3d7bd9",
              "vars": { "root": "D:/Work/A社_MES改造" },
              "buttons": [
                { "id": "a-mes-001", "row": 1, "col": 1, "label": "01_仕様書",
                  "action": { "type": "open_folder", "path": "${root}/01_仕様書" } },
                { "id": "a-mes-002", "row": 1, "col": 2, "label": "I/O一覧",
                  "action": { "type": "open_file", "path": "${root}/02_設計/IO一覧.xlsx" } },
                { "id": "a-mes-005", "row": 1, "col": 5, "label": "AE86",
                  "image": { "url": "https://upload.wikimedia.org/wikipedia/commons/a/ae/AE86.jpg", "query": "Toyota AE86" },
                  "action": { "type": "open_folder", "path": "D:/Work/AE86" } },
                { "id": "a-mes-011", "row": 3, "col": 1, "label": "作業開始",
                  "image": { "color": "#f0a035" },
                  "action": { "type": "multi", "delay_ms": 500, "steps": [
                      { "type": "open_folder", "path": "${root}" },
                      { "type": "open_file", "path": "${root}/作業日報.xlsx" },
                      { "type": "run", "path": "C:/Program Files (x86)/MELSOFT/Navigator/GXW3.exe" } ] } }
              ]
            }
          ],
          "dials": [
            { "index": 1, "label": "音量",   "function": "master_volume", "step": 2 },
            { "index": 2, "label": "明るさ", "function": "brightness",    "step": 5 },
            { "index": 3, "label": "マイク", "function": "mic_volume",    "step": 2 }
          ]
        }
        """;
}
