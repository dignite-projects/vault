import { mapEnumToOptions } from '@abp/ng.core';

export enum SubmissionFileKind {
  Markdown = 0,
  Original = 1,
}

export const submissionFileKindOptions = mapEnumToOptions(SubmissionFileKind);
