import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { IndexSearchTestInput, IndexSearchTestResultDto } from '../../search/models';

@Injectable({
  providedIn: 'root',
})
export class IndexSearchTestService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  
  run = (input: IndexSearchTestInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexSearchTestResultDto>({
      method: 'POST',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/search-test',
      body: input,
    },
      { apiName: this.apiName,...config });
}
