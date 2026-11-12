import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DocumentOutlineDto, IndexReadInput, IndexReadResultDto, IndexSearchInput, IndexSearchResultDto } from '../../search/models';

@Injectable({
  providedIn: 'root',
})
export class IndexSearchService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  
  getOutline = (documentId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentOutlineDto>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: `/api/vault-index/documents/${documentId}/outline`,
    },
      { apiName: this.apiName,...config });
  

  
  read = (input: IndexReadInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexReadResultDto>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/read',
      params: { pointer: input.pointer },
    },
      { apiName: this.apiName,...config });
  

  
  search = (input: IndexSearchInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexSearchResultDto>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/search',
      params: { query: input.query, documentTypeCode: input.documentTypeCode, maxResultCount: input.maxResultCount, fieldFilters: input.fieldFilters },
    },
      { apiName: this.apiName,...config });
}
