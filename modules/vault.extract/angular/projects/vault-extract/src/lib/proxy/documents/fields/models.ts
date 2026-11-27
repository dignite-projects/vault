import type { EntityDto } from '@abp/ng.core';
import type { FieldValidationRuleOutcome } from './field-validation-rule-outcome.enum';

export interface CreateFieldDefinitionDto {
  documentTypeId: string;
  name: string;
  displayName: string;
  description?: string | null;
  fieldTypeName: string;
  configuration?: Record<string, object> | null;
  displayOrder?: number;
  isRequired?: boolean;
  isSearchable?: boolean;
  isUniqueKey?: boolean;
  validationRules?: FieldValidationRuleDto[] | null;
}

export interface DraftFieldDefinitionInput {
  prompt: string;
  forNewField?: boolean;
}

export interface DraftFieldValidationRuleInput {
  fieldId?: string;
  description: string;
}

export interface DraftFieldValidationRulesFromPromptInput {
  fieldId?: string;
}

export interface FieldDefinitionDraftDto {
  displayName?: string;
  name?: string;
  fieldTypeName?: string;
  configuration?: Record<string, object>;
  isRequired?: boolean;
}

export interface FieldDefinitionDto extends EntityDto<string> {
  tenantId?: string | null;
  documentTypeId?: string;
  name?: string;
  displayName?: string;
  description?: string | null;
  fieldTypeName?: string;
  configuration?: Record<string, object>;
  displayOrder?: number;
  isRequired?: boolean;
  isSearchable?: boolean;
  isUniqueKey?: boolean;
  validationRules?: FieldValidationRuleDto[];
}

export interface FieldPromptPolishInput {
  prompt: string;
}

export interface FieldPromptPolishResultDto {
  prompt?: string;
}

export interface FieldTypeDto {
  name?: string;
  indexable?: boolean;
}

export interface FieldValidationRuleDto {
  description: string;
  expression: string;
  inputs?: FieldValidationRuleInputDto[];
}

export interface FieldValidationRuleInputDto {
  alias: string;
  fieldId: string;
}

export interface FieldValidationRuleTestResultDto {
  outcome?: FieldValidationRuleOutcome;
  detail?: string | null;
}

export interface GetFieldDefinitionListInput {
  documentTypeId?: string | null;
  onlyDeleted?: boolean;
}

export interface TestFieldValidationRulesInput {
  fieldId?: string;
  documentId?: string;
  rules: FieldValidationRuleDto[];
}

export interface UpdateFieldDefinitionDto {
  name: string;
  displayName: string;
  description?: string | null;
  fieldTypeName: string;
  configuration?: Record<string, object> | null;
  displayOrder?: number;
  isRequired?: boolean;
  isSearchable?: boolean;
  isUniqueKey?: boolean;
  validationRules?: FieldValidationRuleDto[] | null;
}
