import { Location } from '@angular/common';
import { Router } from '@angular/router';
import { Product } from './../models/product';
import { CacheService } from './../services/cache.service';
import { ChangeDetectorRef, Component, OnInit } from "@angular/core";
import { Context } from "../core/context";
import { SupplierSyncService } from '../services/supplierSync';
import { ProductsService } from '../services/products';
import { SyncResponse } from '../models/syncResponse';
import { SupplierProduct } from '../suppliers/commonTypes';
import { SyncEventService } from '../services/syncEvent';
import { AlertService } from '../services/alert';
import { Observable, forkJoin, Subject, Subscription } from 'rxjs';
import { CustomerService } from '../services/customer';
import { GeneralParser } from '../suppliers/parser';
import { debounceTime } from 'rxjs/operators';

@Component({
    templateUrl: "./customerSync.html",
    selector: "customer-sync"
})
export class CustomerSync implements OnInit {
    syncIntervalId: any;
    toggleWindow: boolean = false;
    parsingAndSavingData: boolean = false;
    requeryProducts: boolean = false;
    suppliers: any = [];
    suplierCustomer: any = [];
    dataReadyToParse: boolean = false;
    
    favoritesSyncRunning: boolean = false;
    favoritesSyncComplete: any = [];
    
    allowFullSync: boolean = false;
    fullSyncComplete: any = [];
    fullSyncRunning: boolean = false;
    
    substituteSyncRunning: boolean = false;
    substituteSyncComplete: any = [];

    invoiceSyncRunning: boolean = false;
    invoiceSyncComplete: any = [];

    parser: Worker = null;
    showMenu = true;

    syncTimer: any;

    private refreshProductsSubject: Subject<string> = new Subject<string>();
    private refreshProductsSubscription: Subscription;


    constructor(private supplierSyncService: SupplierSyncService,
        private productsService: ProductsService,
        private syncEventService: SyncEventService,
        public alertService: AlertService,
        public customerService: CustomerService,
        private cacheService: CacheService,
        public location: Location,
        private router: Router,
        public cdr: ChangeDetectorRef) {
    }

    public ngOnInit(): void {
        this.router.events.subscribe((val) => {
            if (location.pathname != '') {
                if (location.pathname == '/fastorder') {
                    this.showMenu = false;
                } else {
                    this.showMenu = true;
                }
            }
        });

        this.initializeRefreshProducts();
        this.getSyncStatus();

        if (Context.initialLogin == true
            && Context.performFavoritesSyncOnInitialLogin == true) {
            this.requestSync(null, true, false, false, []);
        };

        this.syncEventService.supplierUpdateEvent.subscribe(data => {
            this.getSyncStatus();
        });

        this.syncEventService.syncUpdateEvent.subscribe(data => {
            this.getSyncStatus();
        });

        this.syncEventService.credentialVerificationEvent.subscribe(supplierId => {
            this.requestSync(Number(supplierId), true, false, false, []);
        });

        this.syncEventService.substituteItemsSyncEvent.subscribe(data => {
            this.requestSync(null, false, true, false, data);
        });
    }

    initializeRefreshProducts() {
        this.refreshProductsSubscription = this.refreshProductsSubject.pipe(
            debounceTime(10000)
          ).subscribe((data) => {
                this.cacheService.getProducts(true);
                this.syncEventService.syncEvent.emit("products saved");
            });
    }

    startSyncCheck() {
        this.syncIntervalId = setInterval(() => {
            this.getSyncStatus();
        }, Context.syncRefreshSeconds * 1000);
    }

    stopSyncCheck() {
        clearInterval(this.syncIntervalId);
        this.syncIntervalId = null;
    }

    cancelFavoritesSync(supplier) {
        this.supplierSyncService.cancelSync(
            {
                customerId: Context.customerId,
                supplierId: supplier.supplierId,
                requestId: supplier.favoritesSyncRequestId
            }
        ).subscribe(res => {
            this.getSyncStatus(true, false, supplier.supplierId);
        });
    }

    cancelFullSync(supplier) {
        this.supplierSyncService.cancelSync(
            {
                customerId: Context.customerId,
                supplierId: supplier.supplierId,
                requestId: supplier.fullSyncRequestId
            }
        ).subscribe(res => {
            this.getSyncStatus(false, true, false, supplier.supplierId);
        });
    }

    cancelSubstituteSync(supplier) {
        this.supplierSyncService.cancelSync(
            {
                customerId: Context.customerId,
                supplierId: supplier.supplierId,
                requestId: supplier.substituteSyncRequestId
            }
        ).subscribe(res => {
            this.getSyncStatus(false, false, true, supplier.supplierId);
        });
    }

    cancelInvoiceSync(supplier) {
        this.supplierSyncService.cancelSync(
            {
                customerId: Context.customerId,
                supplierId: supplier.supplierId,
                requestId: supplier.substituteSyncRequestId
            }
        ).subscribe(res => {
            this.getSyncStatus(false, false, true, supplier.supplierId);
        });
    }

    getSyncStatus(cancelFavoriteSync: boolean = false, cancelFullSync: boolean = false, cancelSubstituteSync: boolean = false, cancelInvoiceSync: boolean = false, supplierId: number = 0) {
        this.supplierSyncService.getSyncStatus({ customerId: Context.customerId }).then(res => {
            if (res != null) {
                this.suppliers = res;

                // initialize status of each supplier
                this.suppliers.forEach(supplier => {
                    if (this.favoritesSyncComplete.filter(i => i.id == supplier.supplierId).length == 0) {
                        this.favoritesSyncComplete.push(
                            {
                                id: supplier.supplierId,
                                status: 0
                            });
                    }

                    if (this.fullSyncComplete.filter(i => i.id == supplier.supplierId).length == 0) {
                        this.fullSyncComplete.push(
                            {
                                id: supplier.supplierId,
                                status: 0
                            });
                    }

                    if (this.substituteSyncComplete.filter(i => i.id == supplier.supplierId).length == 0) {
                        this.substituteSyncComplete.push(
                            {
                                id: supplier.supplierId,
                                status: 0
                            });
                    }

                    if (this.invoiceSyncComplete.filter(i => i.id == supplier.supplierId).length == 0) {
                        this.invoiceSyncComplete.push(
                            {
                                id: supplier.supplierId,
                                status: 0
                            });
                    }
                });

                // if there is data is ready to parse then parse it
                this.dataReadyToParse = this.suppliers.filter(c => c.dataReadyToParse === true).length > 0;
                this.favoritesSyncRunning = this.suppliers.filter(c => c.favoritesRunning === true).length > 0;
                this.allowFullSync = this.suppliers.filter(c => c.allowFullSync === true).length > 0;
                this.fullSyncRunning = this.suppliers.filter(c => c.fullSyncRunning === true).length > 0;
                this.substituteSyncRunning = this.suppliers.filter(c => c.substituteSyncRunning === true).length > 0;
                this.invoiceSyncRunning = this.suppliers.filter(c => c.invoiceSyncRunning === true).length > 0;

                // set status values for icons
                this.suppliers.forEach(element => {
                    // if running, set status = 1
                    // if cancelled, set status = 0
                    // if no longer running and was previously running or last sync date = today, set status = 2

                    var forceCheck = false;

                    // fav sync
                    var favSync = this.favoritesSyncComplete.find(i => i.id === element.supplierId);
                    if (element.favoritesRunning === true) {
                        favSync.status = 1;
                        if (element.favoritesStatusMessage == null 
                            || element.favoritesStatusMessage == '') {
                            element.favoritesStatusMessage = 'Pending';
                        }
                    }
                    else if (cancelFavoriteSync === true && element.supplierId == supplierId) {
                        favSync.status = 0;
                    }
                    else if (element.favoritesRunning === false && favSync.status == 1) {
                        favSync.status = 2;
                        this.requeryProducts = true;
                    }
                    else if ((new Date()).toDateString() === (new Date(element.favoritesLastSyncDate)).toDateString()) {                    
                        favSync.status = 2;
                    }
                    else if (element.favoritesRunning === false) {
                        favSync.status = 0;
                    }

                    // full sync
                    var fullSync = this.fullSyncComplete.find(i => i.id === element.supplierId);
                    if (element.fullSyncRunning === true) {
                        fullSync.status = 1;
                        if (element.fullSyncStatusMessage == null 
                            || element.fullSyncStatusMessage == '') {
                            element.fullSyncStatusMessage = 'Pending';
                        }
                    }
                    else if (cancelFullSync === true && element.supplierId == supplierId) {
                        fullSync.status = 0;
                    }
                    else if (element.fullSyncRunning === false && fullSync.status == 1) {
                        fullSync.status = 2;
                        this.requeryProducts = true;
                    }
                    else if ((new Date()).toDateString() === (new Date(element.fullSyncLastSyncDate)).toDateString()) {
                        fullSync.status = 2;
                    }
                    else if (element.fullSyncRunning === false) {
                        fullSync.status = 0;
                    }
                    
                    // substitute sync
                    var substituteSync = this.substituteSyncComplete.find(i => i.id === element.supplierId);
                    if (element.substituteSyncRunning === true) {
                        substituteSync.status = 1;
                    }
                    else if (cancelSubstituteSync === true && element.supplierId == supplierId) {
                        substituteSync.status = 0;
                    }
                    else if (element.substituteSyncRunning === false && substituteSync.status == 1) {
                        substituteSync.status = 2;
                    }
                    else if (element.substituteSyncRunning === false) {
                        substituteSync.status = 0;
                    }

                    // customer sync
                    var invoiceSync = this.invoiceSyncComplete.find(i => i.id === element.supplierId);
                    if (element.customerRunning === true) {
                        favSync.status = 1;
                        if (element.customerStatusMessage == null 
                            || element.customerStatusMessage == '') {
                            element.customerStatusMessage = 'Pending';
                        }
                    }
                    else if (cancelInvoiceSync === true && element.supplierId == supplierId) {
                        invoiceSync.status = 0;
                    }
                    else if (element.customerRunning === false && invoiceSync.status == 1) {
                        invoiceSync.status = 2;
                        this.requeryProducts = true;
                    }
                    else if ((new Date()).toDateString() === (new Date(element.customerLastSyncDate)).toDateString()) {                    
                        invoiceSync.status = 2;
                    }
                    else if (element.customerRunning === false) {
                        invoiceSync.status = 0;
                    }
                });

                if (this.dataReadyToParse
                    && this.parsingAndSavingData === false) {
                    this.getDataToParse();
                } else if (this.requeryProducts === true
                    && this.favoritesSyncRunning === false
                    && this.fullSyncRunning === false) {
                    this.requeryProducts = false;
                    this.alertService.clearErrorMessage();
                    this.alertService.clearSuccessMessage();
                    this.alertService.sendSuccessMessage("Sync is now complete");
                    this.refreshProductsSubject.next("");
                }

                // should we continue to check for sync?
                if (this.syncIntervalId == null
                    && (this.favoritesSyncRunning || this.fullSyncRunning || this.substituteSyncRunning || this.invoiceSyncRunning)) {
                    this.startSyncCheck();
                }
                else if (this.syncIntervalId != null
                    && !this.favoritesSyncRunning
                    && !this.fullSyncRunning
                    && !this.substituteSyncRunning
                    && !this.invoiceSyncRunning) {
                    this.stopSyncCheck();
                }

                //manually detect changes
                this.cdr.detectChanges();
            }
        });
    }

    //not sure what to add here for customer sync
    requestSync(supplierId: number, favoritesRunning: boolean, substituteItemsRunning: boolean, fullSyncRunning: boolean, products: Product[]) {
        this.alertService.clearErrorMessage();
        this.supplierSyncService.requestSync({
            customerId: Context.customerId,
            supplierId: supplierId,
            favoritesOnly: favoritesRunning,
            fullSync: fullSyncRunning,
            substituteItemsOnly: substituteItemsRunning,
            products: products
        }).subscribe(res => {
            this.getSyncStatus();
        });
    }

    getDataToParse = () => {
        this.supplierSyncService.getDataToParse(
            {
                customerId: Context.customerId
            }).then(
                res => {
                    if (res != null) {
                        this.parseData(res);
                    }
                    else {
                        this.parsingAndSavingData = false;
                        this.getSyncStatus();
                    }
                }
            );
    }

    public parseData = (syncResponse: SyncResponse) => {
        if (syncResponse != null
            && syncResponse.responseHeader != null
            && syncResponse.responseHeader.supplierId) {

            try {
                this.parsingAndSavingData = true;
                // remove this when you are done
                // this will bypass the worker process on a separate thread
                var test = false;
                if (!test && typeof Worker !== 'undefined') {
                    if (this.parser == null) {
                        this.parser = new Worker(new URL('src/app/shared/webworker/parser.worker', import.meta.url), { type: `module` });
                    }
                    //not sure what to add here for customer sync
                    this.parser.onmessage = ({ data }) => {
                        this.saveData(
                            data.requestId,
                            data.responseId,
                            data.supplierId,
                            data.parsedProducts,
                            data.favoritesOnly,
                            data.fullSyncOnly,
                            data.substituteItemsOnly,
                            true,
                            false);
                    };
                    this.parser.postMessage(syncResponse);
                }
                //not sure what to add here fro customer sync
                else {
                    var parser = new GeneralParser();
                    var parsedProducts = parser.parseProducts(syncResponse);
                    this.saveData(
                        syncResponse.requestId,
                        syncResponse.responseId,
                        syncResponse.responseHeader.supplierId,
                        parsedProducts,
                        syncResponse.responseHeader.favoritesOnly,
                        syncResponse.responseHeader.fullSyncOnly,
                        syncResponse.responseHeader.substituteItemsOnly,
                        true,
                        false);
                }
            }
            catch (ex) {
                this.parsingAndSavingData = false;
            }
        }
        else {
            this.parsingAndSavingData = false;
        }
    }

    //not sure what to add here for customer sync
    public saveData = (
            requestId: number, 
            responseId: number, 
            supplierId: number, 
            parsedDataToSave: SupplierProduct[], 
            favoriteItemsOnly: boolean,
            fullSyncOnly: boolean,
            substituteItemsOnly: boolean,
            forceSave: boolean, 
            errorGenerated: boolean) => {
        var arraySize = 50;
        if (parsedDataToSave.length > 0 || forceSave) {
            var dataToSave = parsedDataToSave.splice(0, arraySize);
            this.productsService.saveParsedProducts(
                {
                    customerId: Context.customerId,
                    requestId: requestId,
                    responseId: responseId,
                    products: dataToSave,
                    supplierId: supplierId,
                    deleteResponse: parsedDataToSave.length == 0,
                    returnProducts: substituteItemsOnly
                }).subscribe(
                    res => {
                        if (substituteItemsOnly) {
                            var subProducts = res as Product[];
                            if (subProducts.length > 0) {
                                this.syncEventService.substituteItemsSyncCompleteEvent.emit(res as Product[]);
                            } 
                        }
                        //not sure what to add here for customer sync
                        this.saveData(requestId, responseId, supplierId, parsedDataToSave, favoriteItemsOnly, fullSyncOnly, substituteItemsOnly, false, errorGenerated);
                    },
                    error => {
                        //not sure what to add here for customer sync
                        this.saveData(requestId, responseId, supplierId, parsedDataToSave, favoriteItemsOnly, fullSyncOnly, substituteItemsOnly, false, true);
                    }
                )
        }
        else {
            if (errorGenerated == true) {
                this.parsingAndSavingData = false;
                this.supplierSyncService.flagSyncResponseAsError(
                    {
                        customerId: Context.customerId,
                        requestId: requestId,
                        responseId: responseId
                    }).subscribe(
                        res => {
                        },
                        error => {
                        }
                    )
            }
            else {
                if (!substituteItemsOnly) {
                    this.requeryProducts = true;
                }
                this.getDataToParse();
            }
        }
    }

    opencloseToggle(value, option) {
        if (option == 1) {
            this.favoritesSyncComplete.forEach(element => {
                if (element.status == 2) {
                    element.status = 0;
                }
            });
        }
        if (option == 2) {
            this.fullSyncComplete.forEach(element => {
                if (element.status == 2) {
                    element.status = 0;
                }
            });
        }
        if (option == 3) {
            this.invoiceSyncComplete.forEach(element => {
                if (element.status == 2) {
                    element.status = 0;
                }
            });
        }
        this.toggleWindow = value;
    }
}
