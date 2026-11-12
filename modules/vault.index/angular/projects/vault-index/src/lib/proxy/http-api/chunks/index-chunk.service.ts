import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { GetIndexChunkListInput, IndexChunkListDto } from '../../chunks/models';

@Injectable({
  providedIn: 'root',
})
export class IndexChunkService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  
  disable = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-index/chunks/${id}/disable`,
    },
      { apiName: this.apiName,...config });
  

  
  enable = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-index/chunks/${id}/enable`,
    },
      { apiName: this.apiName,...config });
  

  
  getList = (input: GetIndexChunkListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexChunkListDto>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/chunks',
      params: { documentId: input.documentId, skipCount: input.skipCount, maxResultCount: input.maxResultCount },
    },
      { apiName: this.apiName,...config });
}
