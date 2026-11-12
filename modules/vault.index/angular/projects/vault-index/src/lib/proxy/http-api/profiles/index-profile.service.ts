import { RestService, Rest } from '@abp/ng.core';
import type { ListResultDto } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { IndexProfileDto, IndexProfileInput, IndexRebuildEstimateDto } from '../../profiles/models';

@Injectable({
  providedIn: 'root',
})
export class IndexProfileService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  
  getList = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, ListResultDto<IndexProfileDto>>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/profiles',
    },
      { apiName: this.apiName,...config });
  

  
  getRebuildEstimate = (input: IndexProfileInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexRebuildEstimateDto>({
      method: 'GET',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/profiles/rebuild-estimate',
      params: { documentTypeId: input.documentTypeId, chunkingStrategy: input.chunkingStrategy, maxTokensPerChunk: input.maxTokensPerChunk, overlapTokens: input.overlapTokens },
    },
      { apiName: this.apiName,...config });
  

  
  reset = (documentTypeId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/vault-index/profiles/${documentTypeId}`,
    },
      { apiName: this.apiName,...config });
  

  
  set = (input: IndexProfileInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, IndexProfileDto>({
      method: 'PUT',
      headers: { Accept: 'application/json' },
      url: '/api/vault-index/profiles',
      body: input,
    },
      { apiName: this.apiName,...config });
}
