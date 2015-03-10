/// <reference path="Stormancer.ts" />

module Stormancer {
    interface ILogger {
        trace(message: string): void;

        debug(message: string): void;

        error(ex: ExceptionInformation): void;

        error(format: string): void;

        info(format: string): void;
    }
}
