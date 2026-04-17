import * as React from "react";
import { IPublicClientApplication, Logger, InteractionStatus, AccountInfo } from "@azure/msal-browser";
export interface IMsalContext {
    instance: IPublicClientApplication;
    inProgress: InteractionStatus;
    accounts: AccountInfo[];
    logger: Logger;
}
export declare const MsalContext: React.Context<IMsalContext>;
export declare const MsalConsumer: React.Consumer<IMsalContext>;
