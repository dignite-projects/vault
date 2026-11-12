import { RestService, Rest } from '@abp/ng.core';
import type { ListResultDto, PagedResultDto } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { GetIndexedDocumentListInput, IndexRunDto, IndexStatusSummaryDto, IndexedDocumentStatusDto } from '../../status/models';

@Injectable({
  providedIn: 'root',
})
export class IndexStatusService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  
  getDocumentList = (input: GetIndexedDocumentListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, PagedResultDto<IndexedDocumentStatusDto>>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/status/documents',
      params: { status: input.status, filter: input.filter, sorting: input.sorting, skipCount: input.skipCount, maxResultCount: input.maxResultCount },
    },
      { apiName: this.apiName,...config });
  

  
  getRuns = (documentId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ListResultDto<IndexRunDto>>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: `/api/vault-index/status/documents/${documentId}/runs`,
    },
      { apiName: this.apiName,...config });
  

  
  getSummary = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexStatusSummaryDto>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/status',
    },
      { apiName: this.apiName,...config });
  

  
  rebuild = (documentId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-index/status/documents/${documentId}/rebuild`,
    },
      { apiName: this.apiName,...config });
}
