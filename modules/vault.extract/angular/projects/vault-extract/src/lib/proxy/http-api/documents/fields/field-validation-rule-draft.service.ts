import { RestService, Rest } from '@abp/ng.core';
import type { ListResultDto } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DraftFieldValidationRuleInput, DraftFieldValidationRulesFromPromptInput, FieldValidationRuleDto, FieldValidationRuleTestResultDto, TestFieldValidationRulesInput } from '../../../documents/fields/models';

@Injectable({
  providedIn: 'root',
})
export class FieldValidationRuleDraftService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  
  draft = (input: DraftFieldValidationRuleInput, cancellationToken: any, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ListResultDto<FieldValidationRuleDto>>({
      method: 'POST',
      headers: { Accept: 'application/json' },
      url: '/api/vault-extract/field-validation-rule-draft/draft',
      body: input,
    },
      { apiName: this.apiName,...config });
  

  
  draftFromPrompt = (input: DraftFieldValidationRulesFromPromptInput, cancellationToken: any, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ListResultDto<FieldValidationRuleDto>>({
      method: 'POST',
      headers: { Accept: 'application/json' },
      url: '/api/vault-extract/field-validation-rule-draft/draft-from-prompt',
      body: input,
    },
      { apiName: this.apiName,...config });
  

  
  test = (input: TestFieldValidationRulesInput, cancellationToken: any, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ListResultDto<FieldValidationRuleTestResultDto>>({
      method: 'POST',
      headers: { Accept: 'application/json' },
      url: '/api/vault-extract/field-validation-rule-draft/test',
      body: input,
    },
      { apiName: this.apiName,...config });
}
