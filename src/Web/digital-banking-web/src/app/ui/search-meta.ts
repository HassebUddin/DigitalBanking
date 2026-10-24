import { Component, Input } from '@angular/core';

// "SQL Server · 4 provinces · Index seek on CNIC · 12 ms" under a staff list, plus a tip on the fast searches.
@Component({
  selector: 'app-search-meta',
  template: `
    <div class="search-meta">
      <span><b>SQL Server</b> · 4 provinces · {{ strategy || 'Loading…' }} · {{ tookMs }} ms</span>
      <span class="search-tip">{{ tip }} Elasticsearch is planned for full-text name search at 50M scale.</span>
    </div>
  `
})
export class AppSearchMeta {
  @Input() strategy = '';
  @Input() tookMs = 0;
  @Input() tip = '';
}
